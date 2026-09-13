// Phase-0 measurement harness for docs/proposals/attachment-parser-isolation.md.
//
//   gen <outdir>                 write the adversarial fixtures
//   run <outdir> [timeoutSec]    run every (fixture, op) pair in a CHILD process,
//                                polling its RSS, so a crash, hang or OOM kill is
//                                a result row rather than a dead harness
//   child <op> <file>            internal: one parser op, one file
//
// Ops: pdfium-count (PdfRenderer.PageCount), pdfium-render (page 0 → JPEG),
//      pdfpig-extract (the indexer's real AttachmentTextExtractor path).
//
// Run natively for a first look; run inside `docker run --memory=2g` for the
// answer that matters (the embedder's and indexer's compose limit). The runner
// reads the cgroup's oom_kill counter around each child when it exists.

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Mailvec.Core.Attachments;
using Mailvec.Core.Options;
using Mailvec.Pdf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

return args switch
{
    ["gen", var dir] => Generator.WriteAll(dir),
    ["gen-ops", var dir, var n] => Generator.WriteOps(dir, int.Parse(n)),
    ["gen-sh", var dir, var n] => Generator.WriteShading(dir, int.Parse(n)),
    ["gen-paths", var dir, var n] => Generator.WritePaths(dir, int.Parse(n)),
    ["run", var dir] => await Runner.RunAllAsync(dir, 300),
    ["run", var dir, var t] => await Runner.RunAllAsync(dir, int.Parse(t)),
    ["child", var op, var file] => RunChild(op, file),
    _ => Usage(),
};

static int RunChild(string op, string file)
{
    if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        return Child.Run(op, file);
    Console.Error.WriteLine("unsupported platform for the native renderer");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("usage: gen <outdir> | run <outdir> [timeoutSec] | child <op> <file>");
    return 1;
}

// ---------------------------------------------------------------------------

[System.Runtime.Versioning.SupportedOSPlatform("macos")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static class Child
{
    public static int Run(string op, string file)
    {
        var bytes = File.ReadAllBytes(file);
        var sw = Stopwatch.StartNew();
        try
        {
            string detail = op switch
            {
                "pdfium-count" => $"pages={PdfRenderer.PageCount(bytes)}",
                "pdfium-render" => $"jpegBytes={PdfRenderer.RenderPageJpeg(bytes, 0).Length}",
                "pdfpig-extract" => Extract(bytes, file),
                // Three runs in ONE process: does a managed OutOfMemoryException from
                // the first leave the process able to extract the next document?
                "pdfpig-extract-x3" => string.Join(" ", Enumerable.Range(1, 3).Select(i => $"run{i}:{Extract(bytes, file)}")),
                _ => throw new ArgumentException($"unknown op {op}"),
            };
            Console.WriteLine($"RESULT ok ms={sw.ElapsedMilliseconds} {detail}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RESULT error ms={sw.ElapsedMilliseconds} type={ex.GetType().Name}");
            return 2;
        }
    }

    /// <summary>
    /// The indexer's real path: MimePart → AttachmentTextExtractor.Extract,
    /// which catches its own exceptions and reports a status. A crash here is
    /// therefore something the indexer cannot catch either.
    /// </summary>
    private static string Extract(byte[] bytes, string file)
    {
        // Raise the per-attachment ceiling so the size gate doesn't pre-empt
        // the parser — the point is to measure the parser, not the gate.
        var opts = Options.Create(new IndexerOptions { AttachmentMaxBytes = 512L * 1024 * 1024 });
        var extractor = new AttachmentTextExtractor(opts, NullLogger<AttachmentTextExtractor>.Instance);
        var name = Path.GetFileName(file);
        var part = new MimePart("application", "pdf")
        {
            Content = new MimeContent(new MemoryStream(bytes)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            FileName = name,
        };
        var r = extractor.Extract(part, name, "application/pdf", bytes.Length);
        return $"status={r.Status} chars={r.Text?.Length ?? 0}";
    }
}

// ---------------------------------------------------------------------------

static class Runner
{
    private static readonly string[] Ops = ["pdfium-count", "pdfium-render", "pdfpig-extract", "pdfpig-extract-x3"];

    public static async Task<int> RunAllAsync(string dir, int timeoutSec)
    {
        var asm = typeof(Runner).Assembly.Location;
        var fixtures = Directory.GetFiles(dir, "*.pdf").OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (fixtures.Count == 0) { Console.Error.WriteLine($"no fixtures in {dir}; run gen first"); return 1; }

        var env = $"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture} · .NET {Environment.Version} · cgroup memory.max={ReadCgroup("memory.max") ?? "n/a"}";
        Console.WriteLine(env);
        var rows = new List<string>();
        rows.Add($"Environment: {env}  ");
        rows.Add($"Timeout per op: {timeoutSec}s. Peak RSS is polled every 100 ms (VmHWM on Linux, `ps rss` on macOS).");
        rows.Add("");
        rows.Add("| fixture | size | op | outcome | ms | peak RSS MB | detail |");
        rows.Add("| --- | ---: | --- | --- | ---: | ---: | --- |");

        foreach (var f in fixtures)
        {
            var size = new FileInfo(f).Length;
            foreach (var op in Ops)
            {
                var r = await RunChildAsync(asm, op, f, timeoutSec);
                var line = $"| {Path.GetFileName(f)} | {Human(size)} | {op} | {r.Outcome} | {r.Ms} | {r.PeakBytes / (1024.0 * 1024):F0} | {r.Detail} |";
                Console.WriteLine(line);
                rows.Add(line);
            }
        }

        var outPath = Path.Combine(dir, "results.md");
        await File.WriteAllLinesAsync(outPath, rows);
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    private sealed record ChildResult(string Outcome, long Ms, long PeakBytes, string Detail);

    private static async Task<ChildResult> RunChildAsync(string asm, string op, string file, int timeoutSec)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(asm);
        psi.ArgumentList.Add("child");
        psi.ArgumentList.Add(op);
        psi.ArgumentList.Add(file);

        long oomBefore = ReadOomKills();
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        var sw = Stopwatch.StartNew();
        long peak = 0;
        bool timedOut = false;
        while (!p.WaitForExit(100))
        {
            peak = Math.Max(peak, ReadRss(p.Id));
            if (sw.Elapsed.TotalSeconds > timeoutSec)
            {
                timedOut = true;
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                p.WaitForExit();
                break;
            }
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        long oomDelta = ReadOomKills() - oomBefore;

        var result = stdout.Split('\n').FirstOrDefault(l => l.StartsWith("RESULT ", StringComparison.Ordinal));
        long ms = sw.ElapsedMilliseconds;
        string detail = "";
        if (result is not null)
        {
            var parts = result.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var msPart = parts.FirstOrDefault(x => x.StartsWith("ms=", StringComparison.Ordinal));
            if (msPart is not null) ms = long.Parse(msPart[3..]);
            detail = string.Join(' ', parts.Skip(2).Where(x => !x.StartsWith("ms=", StringComparison.Ordinal)));
        }

        string outcome;
        if (timedOut) outcome = $"**TIMEOUT** (killed after {timeoutSec}s)";
        else if (p.ExitCode == 0) outcome = "ok";
        else if (p.ExitCode == 2) outcome = "managed exception";
        else if (oomDelta > 0) outcome = $"**OOM-KILLED** (exit {p.ExitCode}, cgroup oom_kill +{oomDelta})";
        else if (p.ExitCode == 137) outcome = "**KILLED** (SIGKILL, exit 137 — OOM if no cgroup counter)";
        else if (p.ExitCode == 134) outcome = "**CRASH** (SIGABRT, exit 134)";
        else if (p.ExitCode == 139) outcome = "**CRASH** (SIGSEGV, exit 139)";
        else outcome = $"**exit {p.ExitCode}**";

        if (result is null && !string.IsNullOrWhiteSpace(stderr))
        {
            var first = stderr.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
            if (first.Length > 120) first = first[..120] + "…";
            detail = first.Replace('|', '/');
        }
        return new ChildResult(outcome, ms, peak, detail);
    }

    private static long ReadRss(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var line in File.ReadLines($"/proc/{pid}/status"))
                {
                    if (!line.StartsWith("VmHWM:", StringComparison.Ordinal)) continue;
                    var kb = long.Parse(line[6..].Trim().Split(' ')[0]);
                    return kb * 1024;
                }
                return 0;
            }
            var psi = new ProcessStartInfo("ps") { RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("rss="); psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(pid.ToString());
            using var ps = Process.Start(psi)!;
            var s = ps.StandardOutput.ReadToEnd().Trim();
            ps.WaitForExit();
            return long.TryParse(s, out var k) ? k * 1024 : 0;
        }
        catch { return 0; }
    }

    private static string? ReadCgroup(string key)
    {
        try { return File.ReadAllText($"/sys/fs/cgroup/{key}").Trim(); } catch { return null; }
    }

    private static long ReadOomKills()
    {
        var text = ReadCgroup("memory.events");
        if (text is null) return 0;
        foreach (var line in text.Split('\n'))
            if (line.StartsWith("oom_kill ", StringComparison.Ordinal)) return long.Parse(line[9..].Trim());
        return 0;
    }

    private static string Human(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:F0} KB",
        _ => $"{b / (1024.0 * 1024):F1} MB",
    };
}

// ---------------------------------------------------------------------------

static class Generator
{
    public static int WriteAll(string dir)
    {
        Directory.CreateDirectory(dir);
        var repoFixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/Mailvec.Mcp.Tests/Fixtures"));
        if (!Directory.Exists(repoFixtures))
            repoFixtures = Path.GetFullPath("tests/Mailvec.Mcp.Tests/Fixtures");

        // Controls: the repo's own fixtures, untouched.
        Copy(repoFixtures, "text-sample.pdf", dir, "a-ctrl-text.pdf");
        Copy(repoFixtures, "scanned-sample.pdf", dir, "a-ctrl-scanned.pdf");
        Copy(repoFixtures, "digital-table-sample.pdf", dir, "a-ctrl-table.pdf");

        // Malformed: truncated real files and a PDF header followed by noise.
        Truncate(repoFixtures, "text-sample.pdf", dir, "b-trunc-text-50pct.pdf", 0.5);
        Truncate(repoFixtures, "scanned-sample.pdf", dir, "b-trunc-scanned-60pct.pdf", 0.6);
        Truncate(repoFixtures, "digital-table-sample.pdf", dir, "b-trunc-table-50pct.pdf", 0.5);
        Garbage(Path.Combine(dir, "b-garbage-after-header.pdf"));

        // Embedded-raster bombs for PDFium: a flat image compresses ~1000:1, so
        // hundreds of MB of decoded raster fit in a file far under 25 MB.
        ImagePdf(Path.Combine(dir, "c-img-4mp-gray8.pdf"), 2_000, 2_000, "DeviceGray", 1, 8);       // control
        ImagePdf(Path.Combine(dir, "c-img-100mp-rgb8.pdf"), 10_000, 10_000, "DeviceRGB", 3, 8);     // 300 MB raw
        ImagePdf(Path.Combine(dir, "c-img-400mp-gray8.pdf"), 20_000, 20_000, "DeviceGray", 1, 8);   // 400 MB raw
        ImagePdf(Path.Combine(dir, "c-img-900mp-1bit.pdf"), 30_000, 30_000, "DeviceGray", 1, 1);    // 112 MB raw, 900 MP

        // PdfPig stress: one letter object per Tj, millions of them.
        OpsPdf(Path.Combine(dir, "d-ops-1m.pdf"), 1_000_000);
        OpsPdf(Path.Combine(dir, "d-ops-5m.pdf"), 5_000_000);
        // Page-tree stress.
        PagesPdf(Path.Combine(dir, "d-pages-50k.pdf"), 50_000);
        // A form XObject that draws itself — a recursion guard test for both parsers.
        SelfRecursivePdf(Path.Combine(dir, "d-form-self-recursion.pdf"));

        foreach (var f in Directory.GetFiles(dir, "*.pdf").OrderBy(f => f, StringComparer.Ordinal))
            Console.WriteLine($"{new FileInfo(f).Length,12:N0}  {Path.GetFileName(f)}");
        return 0;
    }

    /// <summary>One operator-stress PDF of a chosen size, for scaling runs.</summary>
    public static int WriteOps(string dir, int ops)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"d-ops-{ops / 1_000_000}m.pdf");
        OpsPdf(path, ops);
        Console.WriteLine($"{new FileInfo(path).Length,12:N0}  {Path.GetFileName(path)}");
        return 0;
    }

    /// <summary>
    /// Shading bomb: N full-page axial-gradient fills. PDFium rasterises every
    /// one (CPU-bound, little memory); PdfPig's text extractor has nothing to
    /// collect and returns no_text quickly — i.e. exactly the shape that
    /// passes the indexer and lands on the embedder's OCR pass.
    /// </summary>
    public static int WriteShading(string dir, int n)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"e-sh-{n / 1000}k.pdf");
        var data = Zlib(z =>
        {
            using var w = new StreamWriter(z, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
            for (int i = 0; i < n; i++) w.Write("/Sh0 sh\n");
        });
        var b = new PdfBuilder();
        int cat = b.Reserve(), pages = b.Reserve(), page = b.Reserve();
        int sh = b.Add("<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 612 792] /Extend [true true] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>");
        int content = b.AddStream("/Filter /FlateDecode", data);
        b.Set(cat, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Set(pages, $"<< /Type /Pages /Count 1 /Kids [{page} 0 R] >>");
        b.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Shading << /Sh0 {sh} 0 R >> >> /Contents {content} 0 R >>");
        b.Write(path, cat);
        Console.WriteLine($"{new FileInfo(path).Length,12:N0}  {Path.GetFileName(path)}");
        return 0;
    }

    /// <summary>N stroked diagonal lines: a path object per op in both parsers, no text.</summary>
    public static int WritePaths(string dir, int n)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"e-paths-{n / 1_000_000}m.pdf");
        var data = Zlib(z =>
        {
            using var w = new StreamWriter(z, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
            for (int i = 0; i < n; i++) w.Write("0 0 m 612 792 l S\n");
        });
        var b = new PdfBuilder();
        int cat = b.Reserve(), pages = b.Reserve(), page = b.Reserve();
        int content = b.AddStream("/Filter /FlateDecode", data);
        b.Set(cat, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Set(pages, $"<< /Type /Pages /Count 1 /Kids [{page} 0 R] >>");
        b.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Contents {content} 0 R >>");
        b.Write(path, cat);
        Console.WriteLine($"{new FileInfo(path).Length,12:N0}  {Path.GetFileName(path)}");
        return 0;
    }

    private static void Copy(string srcDir, string name, string dir, string outName) =>
        File.Copy(Path.Combine(srcDir, name), Path.Combine(dir, outName), overwrite: true);

    private static void Truncate(string srcDir, string name, string dir, string outName, double keep)
    {
        var b = File.ReadAllBytes(Path.Combine(srcDir, name));
        File.WriteAllBytes(Path.Combine(dir, outName), b[..(int)(b.Length * keep)]);
    }

    private static void Garbage(string path)
    {
        var rnd = new Random(42);
        var noise = new byte[64 * 1024];
        rnd.NextBytes(noise);
        using var fs = File.Create(path);
        fs.Write("%PDF-1.4\n"u8);
        fs.Write(noise);
    }

    private static void ImagePdf(string path, int w, int h, string colorSpace, int comps, int bpc)
    {
        int rowBytes = (w * comps * bpc + 7) / 8;
        long total = (long)rowBytes * h;
        var data = Zlib(z =>
        {
            var chunk = new byte[1 << 20];
            Array.Fill(chunk, (byte)0xFF); // white
            long left = total;
            while (left > 0)
            {
                int n = (int)Math.Min(chunk.Length, left);
                z.Write(chunk, 0, n);
                left -= n;
            }
        });

        var b = new PdfBuilder();
        int cat = b.Reserve(), pages = b.Reserve(), page = b.Reserve();
        int img = b.AddStream(
            $"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /{colorSpace} /BitsPerComponent {bpc} /Filter /FlateDecode",
            data);
        int content = b.AddStream("", "q 612 0 0 792 0 0 cm /Im0 Do Q"u8.ToArray());
        b.Set(cat, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Set(pages, $"<< /Type /Pages /Count 1 /Kids [{page} 0 R] >>");
        b.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im0 {img} 0 R >> >> /Contents {content} 0 R >>");
        b.Write(path, cat);
    }

    private static void OpsPdf(string path, int ops)
    {
        var data = Zlib(z =>
        {
            using var w = new StreamWriter(z, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
            w.Write("BT /F1 12 Tf 1 0 0 1 10 10 Tm\n");
            for (int i = 0; i < ops; i++) w.Write("(a) Tj\n");
            w.Write("ET\n");
        });
        var b = new PdfBuilder();
        int cat = b.Reserve(), pages = b.Reserve(), page = b.Reserve();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        int content = b.AddStream("/Filter /FlateDecode", data);
        b.Set(cat, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Set(pages, $"<< /Type /Pages /Count 1 /Kids [{page} 0 R] >>");
        b.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {content} 0 R >>");
        b.Write(path, cat);
    }

    private static void PagesPdf(string path, int n)
    {
        var b = new PdfBuilder();
        int cat = b.Reserve(), pages = b.Reserve();
        int font = b.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        int content = b.AddStream("", "BT /F1 12 Tf 1 0 0 1 10 10 Tm (page) Tj ET"u8.ToArray());
        var kids = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            int p = b.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> >> /Contents {content} 0 R >>");
            kids.Append(p).Append(" 0 R ");
        }
        b.Set(cat, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Set(pages, $"<< /Type /Pages /Count {n} /Kids [{kids}] >>");
        b.Write(path, cat);
    }

    private static void SelfRecursivePdf(string path)
    {
        var b = new PdfBuilder();
        int cat = b.Reserve(), pages = b.Reserve(), page = b.Reserve();
        int fx = b.Reserve();
        b.SetStream(fx,
            $"/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /XObject << /Fx {fx} 0 R >> >>",
            "q /Fx Do Q"u8.ToArray());
        int content = b.AddStream("", "q /Fx Do Q"u8.ToArray());
        b.Set(cat, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Set(pages, $"<< /Type /Pages /Count 1 /Kids [{page} 0 R] >>");
        b.Set(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fx {fx} 0 R >> >> /Contents {content} 0 R >>");
        b.Write(path, cat);
    }

    private static byte[] Zlib(Action<Stream> write)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            write(z);
        return ms.ToArray();
    }
}

/// <summary>Minimal classic-xref PDF writer. Object numbers are 1-based insertion order.</summary>
sealed class PdfBuilder
{
    private readonly List<byte[]?> _objects = [];

    public int Reserve() { _objects.Add(null); return _objects.Count; }
    public int Add(string dict) { _objects.Add(Encoding.ASCII.GetBytes(dict)); return _objects.Count; }
    public int AddStream(string dictBody, byte[] data) { _objects.Add(BuildStream(dictBody, data)); return _objects.Count; }
    public void Set(int num, string dict) => _objects[num - 1] = Encoding.ASCII.GetBytes(dict);
    public void SetStream(int num, string dictBody, byte[] data) => _objects[num - 1] = BuildStream(dictBody, data);

    private static byte[] BuildStream(string dictBody, byte[] data)
    {
        var head = Encoding.ASCII.GetBytes($"<< {dictBody} /Length {data.Length} >>\nstream\n");
        var tail = "\nendstream"u8.ToArray();
        var buf = new byte[head.Length + data.Length + tail.Length];
        head.CopyTo(buf, 0);
        data.CopyTo(buf, head.Length);
        tail.CopyTo(buf, head.Length + data.Length);
        return buf;
    }

    public void Write(string path, int rootObj)
    {
        using var fs = File.Create(path);
        void W(string s) => fs.Write(Encoding.ASCII.GetBytes(s));
        W("%PDF-1.5\n");
        fs.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A });
        var offsets = new long[_objects.Count];
        for (int i = 0; i < _objects.Count; i++)
        {
            offsets[i] = fs.Position;
            W($"{i + 1} 0 obj\n");
            fs.Write(_objects[i] ?? throw new InvalidOperationException($"object {i + 1} reserved but never set"));
            W("\nendobj\n");
        }
        long xref = fs.Position;
        W($"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) W($"{off:D10} 00000 n \n");
        W($"trailer\n<< /Size {_objects.Count + 1} /Root {rootObj} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
