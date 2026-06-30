using System.Runtime.Versioning;
using System.Text;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Mcp.Tools;
using Mailvec.Pdf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using SkiaSharp;
using Xunit.Abstractions;

namespace Mailvec.Mcp.Tests.Tools;

/// <summary>
/// End-to-end tests for the PDF page-image tool. Beyond the plumbing guards,
/// these validate the *render itself* by decoding the returned PNG and
/// inspecting pixels — without fragile golden-image diffs (which flake across
/// PDFium/SkiaSharp versions and mac-vs-Linux font rendering). The key checks:
/// dimensions track page-size × DPI, the image isn't blank, and — crucially —
/// the right page's pixels come back (proved with a 2-page PDF whose pages
/// carry a black rectangle in opposite corners). Committed real fixtures add
/// real-world fidelity; see Fixtures/README.md.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public class GetAttachmentPageImageToolTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _maildirRoot;
    private readonly string _downloadDir;
    private readonly ITestOutputHelper _output;

    public GetAttachmentPageImageToolTests(ITestOutputHelper output)
    {
        _output = output;
        _tempRoot = Path.Combine(Path.GetTempPath(), "mailvec-mcp-pageimg-" + Guid.NewGuid().ToString("N"));
        _maildirRoot = Path.Combine(_tempRoot, "Mail");
        _downloadDir = Path.Combine(_tempRoot, "downloads");
        Directory.CreateDirectory(Path.Combine(_maildirRoot, "INBOX", "cur"));
        Directory.CreateDirectory(_downloadDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private GetAttachmentPageImageTool Build(TempDatabase db)
    {
        var ingest = Options.Create(new IngestOptions { MaildirRoot = _maildirRoot });
        var mcp = Options.Create(new McpOptions { AttachmentDownloadDir = _downloadDir });
        return new GetAttachmentPageImageTool(
            new MessageRepository(db.Connections), new AttachmentExtractor(ingest, mcp), Helpers.NoopLogger());
    }

    // ---------- native-load smoke test ----------

    [Fact]
    public void PdfRenderer_reports_page_count_and_emits_a_jpeg()
    {
        var pdf = MinimalPdf(pages: 2);

        PdfRenderer.PageCount(pdf).ShouldBe(2);

        var jpeg = PdfRenderer.RenderPageJpeg(pdf, pageIndex: 0);
        // JPEG signature: FF D8 FF.
        jpeg.AsSpan(0, 3).ToArray().ShouldBe(new byte[] { 0xFF, 0xD8, 0xFF });
    }

    // ---------- guards ----------

    [Fact]
    public void Throws_when_neither_id_nor_messageId_provided()
    {
        using var db = new TempDatabase();
        Should.Throw<McpException>(() => Build(db).GetAttachmentPageImage(partIndex: 0));
    }

    [Fact]
    public void Throws_when_both_id_and_messageId_provided()
    {
        using var db = new TempDatabase();
        var ex = Should.Throw<McpException>(() => Build(db).GetAttachmentPageImage(partIndex: 0, id: 1, messageId: "x@y"));
        ex.Message.ShouldContain("OR");
    }

    [Fact]
    public void Throws_when_page_below_one()
    {
        using var db = new TempDatabase();
        var ex = Should.Throw<McpException>(() => Build(db).GetAttachmentPageImage(partIndex: 0, page: 0, id: 1));
        ex.Message.ShouldContain("page");
    }

    [Fact]
    public void Throws_when_attachment_is_not_a_pdf()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        StageCsv(repo);

        var ex = Should.Throw<McpException>(() => Build(db).GetAttachmentPageImage(partIndex: 0, id: 1));
        ex.Message.ShouldContain("not a PDF");
    }

    [Fact]
    public void Throws_when_page_out_of_range()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = StagePdf(repo, MinimalPdf(pages: 1));

        var ex = Should.Throw<McpException>(() => Build(db).GetAttachmentPageImage(partIndex: 0, page: 2, id: id));
        ex.Message.ShouldContain("out of range");
    }

    [Fact]
    public void Lookup_by_messageId_works_identically()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        StagePdf(repo, MinimalPdf(pages: 1), id: "bymid@x", file: "bymid.eml");

        var result = Build(db).GetAttachmentPageImage(partIndex: 0, messageId: "bymid@x");
        result.Content.OfType<ImageContentBlock>().ShouldHaveSingleItem();
    }

    // ---------- render fidelity (pixel-level) ----------

    [Fact]
    public void Selects_the_requested_page_proven_by_distinct_per_page_content()
    {
        // 2-page PDF: page 1 has a black rectangle in the TOP-LEFT, page 2 in
        // the BOTTOM-RIGHT. Rendering each must light up the matching corner and
        // leave the opposite corner blank — proving the 1-based page param maps
        // to the right pixels (not just the right number in the summary text).
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = StagePdf(repo, QuadrantPdf(), id: "quad@x", file: "quad.eml");
        var tool = Build(db);

        using var p1 = DecodeImage(tool.GetAttachmentPageImage(partIndex: 0, page: 1, id: id));
        DarkFraction(p1, 0.10, 0.10, 0.40, 0.40).ShouldBeGreaterThan(0.8); // top-left inked
        DarkFraction(p1, 0.60, 0.60, 0.90, 0.90).ShouldBeLessThan(0.05);   // bottom-right blank

        using var p2 = DecodeImage(tool.GetAttachmentPageImage(partIndex: 0, page: 2, id: id));
        DarkFraction(p2, 0.60, 0.60, 0.90, 0.90).ShouldBeGreaterThan(0.8); // bottom-right inked
        DarkFraction(p2, 0.10, 0.10, 0.40, 0.40).ShouldBeLessThan(0.05);   // top-left blank
    }

    [Fact]
    public void Rendered_image_dimensions_track_page_size_and_dpi()
    {
        // 300pt square page at the renderer's 150 DPI -> 300/72*150 = 625 px.
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = StagePdf(repo, QuadrantPdf(), id: "dim@x", file: "dim.eml");

        using var bmp = DecodeImage(Build(db).GetAttachmentPageImage(partIndex: 0, page: 1, id: id));
        bmp.Width.ShouldBeInRange(615, 635);
        bmp.Height.ShouldBeInRange(615, 635);
    }

    [Fact]
    public void Large_media_box_is_capped_to_a_sane_long_edge()
    {
        // A page with a huge MediaBox (e.g. a high-res scan) must not render to a
        // multi-thousand-pixel image — the long edge is capped so the MCP payload
        // stays small and matches what Claude downsamples to anyway. Regression
        // for a real scan that rendered 5304x6908 (~20 MB) before the cap.
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = StagePdf(repo, ContentPdf(["0 0 0 rg\n0 0 200 200 re\nf\n"], 3000, 4000), id: "big@x", file: "big.eml");

        using var bmp = DecodeImage(Build(db).GetAttachmentPageImage(partIndex: 0, page: 1, id: id));
        Math.Max(bmp.Width, bmp.Height).ShouldBeLessThanOrEqualTo(PdfRenderer.MaxEdgePx);
        Math.Max(bmp.Width, bmp.Height).ShouldBeGreaterThan(1200); // still a usable resolution
    }

    // ---------- committed real fixtures ----------

    [Fact]
    public void Renders_the_committed_text_fixture_with_visible_ink()
    {
        var pdf = LoadFixture("text-sample.pdf");
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = StagePdf(repo, pdf, id: "textfix@x", file: "textfix.eml");

        using var bmp = DecodeImage(Build(db).GetAttachmentPageImage(partIndex: 0, page: 1, id: id));
        // US Letter (612x792pt) @ 150 DPI is 1275x1650; the 1650 long edge exceeds
        // the 1536 cap, so it's scaled to fit -> ~1187x1536.
        bmp.Height.ShouldBeInRange(1525, 1540);
        bmp.Width.ShouldBeInRange(1175, 1200);
        // Real Helvetica glyphs render as ink; a blank page would be ~0.
        DarkFraction(bmp, 0, 0, 1, 1).ShouldBeGreaterThan(0.0005);
    }

    [Fact]
    public void Renders_the_scanned_fixture_which_text_extraction_cannot_read()
    {
        // Pending until a real scanned/image-only PDF is dropped in Fixtures/
        // (see Fixtures/README.md). Until then this passes as a no-op.
        if (!FixtureExists("scanned-sample.pdf"))
        {
            _output.WriteLine("SKIPPED: Fixtures/scanned-sample.pdf not present — see Fixtures/README.md.");
            return;
        }

        var pdf = LoadFixture("scanned-sample.pdf");

        // The marquee case: it renders to a non-blank image even though it has
        // no text layer.
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = StagePdf(repo, pdf, id: "scanfix@x", file: "scanfix.eml");
        using var bmp = DecodeImage(Build(db).GetAttachmentPageImage(partIndex: 0, page: 1, id: id));
        DarkFraction(bmp, 0, 0, 1, 1).ShouldBeGreaterThan(0.001);

        // Paired contract: the text path returns no_text — which is *why* the
        // image tool exists.
        var part = new MimeKit.MimePart("application", "pdf")
        {
            Content = new MimeKit.MimeContent(new MemoryStream(pdf)),
        };
        var textExtractor = new AttachmentTextExtractor(
            Options.Create(new IndexerOptions()), NullLogger<AttachmentTextExtractor>.Instance);
        textExtractor.Extract(part, "scanned-sample.pdf", "application/pdf", pdf.LongLength)
            .Status.ShouldBe(AttachmentTextExtractor.StatusNoText);
    }

    [Fact]
    public void Renders_a_real_digital_table_pdf_whose_text_is_also_extractable()
    {
        // A real digital invoice (embedded fonts, a logo, a vector table) — the
        // "text -> image" path most mail attachments actually use, which neither
        // the bare-Helvetica fixture nor the scanned ("image -> image") fixture
        // exercises. Unlike the scan, its text IS extractable ('done'); the image
        // is what preserves the column layout that flattened text loses.
        var pdf = LoadFixture("digital-table-sample.pdf");

        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = StagePdf(repo, pdf, id: "tablefix@x", file: "tablefix.eml");

        using var bmp = DecodeImage(Build(db).GetAttachmentPageImage(partIndex: 0, page: 1, id: id));
        // US Letter @150dpi capped to a 1536 long edge -> ~1187x1536.
        bmp.Height.ShouldBeInRange(1525, 1540);
        DarkFraction(bmp, 0, 0, 1, 1).ShouldBeGreaterThan(0.005); // table + text + logo = plenty of ink

        // Paired contrast with the scanned fixture: this one's text extracts cleanly.
        var part = new MimeKit.MimePart("application", "pdf")
        {
            Content = new MimeKit.MimeContent(new MemoryStream(pdf)),
        };
        var textExtractor = new AttachmentTextExtractor(
            Options.Create(new IndexerOptions()), NullLogger<AttachmentTextExtractor>.Instance);
        var extraction = textExtractor.Extract(part, "digital-table-sample.pdf", "application/pdf", pdf.LongLength);
        extraction.Status.ShouldBe(AttachmentTextExtractor.StatusDone);
        extraction.Text.ShouldNotBeNull();
        extraction.Text!.ShouldContain("Home Assistant Cloud");
    }

    // ---------- image inspection ----------

    private static SKBitmap DecodeImage(CallToolResult result)
    {
        var image = result.Content.OfType<ImageContentBlock>().ShouldHaveSingleItem();
        image.MimeType.ShouldBe("image/jpeg");
        // Data is the UTF-8 bytes of the base64 string (SDK quirk), not raw bytes.
        var bytes = Convert.FromBase64String(Encoding.UTF8.GetString(image.Data.Span));
        var bmp = SKBitmap.Decode(bytes);
        bmp.ShouldNotBeNull();
        return bmp;
    }

    /// <summary>Fraction of (strided) pixels in a fractional sub-rectangle that are opaque and dark.</summary>
    private static double DarkFraction(SKBitmap b, double fx0, double fy0, double fx1, double fy1)
    {
        int x0 = (int)(b.Width * fx0), x1 = (int)(b.Width * fx1);
        int y0 = (int)(b.Height * fy0), y1 = (int)(b.Height * fy1);
        int dark = 0, total = 0;
        for (int y = y0; y < y1; y += 2)
        {
            for (int x = x0; x < x1; x += 2)
            {
                var c = b.GetPixel(x, y);
                double lum = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
                if (c.Alpha > 10 && lum < 128) dark++;
                total++;
            }
        }
        return total == 0 ? 0 : (double)dark / total;
    }

    // ---------- staging ----------

    private long StagePdf(MessageRepository repo, byte[] pdf, string id = "pdf@x", string file = "p.eml")
    {
        WriteEml(file, id, "application/pdf", "doc.pdf", Convert.ToBase64String(pdf), base64: true);
        return repo.Upsert(
            Helpers.Sample(id, attachments: [new ParsedAttachment(0, "doc.pdf", "application/pdf", pdf.LongLength)]),
            "INBOX", "INBOX/cur", file, DateTimeOffset.UtcNow);
    }

    private long StageCsv(MessageRepository repo, string id = "csv@x", string file = "c.eml")
    {
        WriteEml(file, id, "text/csv", "data.csv", "a,b\n1,2\n", base64: false);
        return repo.Upsert(
            Helpers.Sample(id, attachments: [new ParsedAttachment(0, "data.csv", "text/csv", 8L)]),
            "INBOX", "INBOX/cur", file, DateTimeOffset.UtcNow);
    }

    private void WriteEml(string file, string id, string contentType, string fileName, string payload, bool base64)
    {
        var enc = base64 ? "Content-Transfer-Encoding: base64\n" : "";
        var eml =
            "Message-ID: <" + id + ">\n" +
            "From: a@x\n" +
            "To: b@x\n" +
            "Subject: doc\n" +
            "MIME-Version: 1.0\n" +
            "Content-Type: multipart/mixed; boundary=\"outer\"\n" +
            "\n" +
            "--outer\n" +
            "Content-Type: text/plain; charset=utf-8\n" +
            "\n" +
            "See attached.\n" +
            "--outer\n" +
            "Content-Type: " + contentType + "; name=\"" + fileName + "\"\n" +
            "Content-Disposition: attachment; filename=\"" + fileName + "\"\n" +
            enc +
            "\n" +
            payload + "\n" +
            "--outer--\n";
        File.WriteAllText(Path.Combine(_maildirRoot, "INBOX", "cur", file), eml);
    }

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static bool FixtureExists(string name) => File.Exists(Path.Combine(FixtureDir, name));
    private static byte[] LoadFixture(string name) => File.ReadAllBytes(Path.Combine(FixtureDir, name));

    // ---------- generated PDFs ----------

    /// <summary>2-page PDF: page 1 fills the top-left quadrant black, page 2 the bottom-right.</summary>
    private static byte[] QuadrantPdf()
    {
        const int W = 300, H = 300;
        var pages = new[]
        {
            $"0 0 0 rg\n0 {H / 2} {W / 2} {H / 2} re\nf\n",     // PDF coords: origin bottom-left -> top-left
            $"0 0 0 rg\n{W / 2} 0 {W / 2} {H / 2} re\nf\n",     // bottom-right
        };
        return ContentPdf(pages, W, H);
    }

    /// <summary>
    /// Build a valid multi-page PDF where each page has a content stream of
    /// drawing operators. xref offsets computed from real byte positions
    /// (all-ASCII, so string length == byte offset) so PDFium parses cleanly.
    /// </summary>
    private static byte[] ContentPdf(IReadOnlyList<string> pageOps, int w, int h)
    {
        int n = pageOps.Count;
        var pageObjNums = Enumerable.Range(0, n).Select(i => 3 + 2 * i).ToArray();
        var objects = new List<string>
        {
            "<</Type /Catalog /Pages 2 0 R>>",
            $"<</Type /Pages /Kids [{string.Join(" ", pageObjNums.Select(p => $"{p} 0 R"))}] /Count {n}>>",
        };
        for (int i = 0; i < n; i++)
        {
            int contentNum = 4 + 2 * i;
            objects.Add($"<</Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Contents {contentNum} 0 R>>");
            objects.Add($"<</Length {Encoding.ASCII.GetByteCount(pageOps[i])}>>\nstream\n{pageOps[i]}\nendstream");
        }
        return Assemble(objects);
    }

    /// <summary>Minimal valid PDF with <paramref name="pages"/> blank pages (no content streams).</summary>
    private static byte[] MinimalPdf(int pages)
    {
        var objects = new List<string>
        {
            "<</Type /Catalog /Pages 2 0 R>>",
            $"<</Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{3 + i} 0 R"))}] /Count {pages}>>",
        };
        for (int i = 0; i < pages; i++)
            objects.Add("<</Type /Page /Parent 2 0 R /MediaBox [0 0 200 200]>>");
        return Assemble(objects);
    }

    private static byte[] Assemble(List<string> objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n");
        sb.Append($"0 {objects.Count + 1}\n");
        sb.Append("0000000000 65535 f \n");
        foreach (var off in offsets)
            sb.Append(off.ToString("D10") + " 00000 n \n");
        sb.Append($"trailer\n<</Size {objects.Count + 1} /Root 1 0 R>>\n");
        sb.Append($"startxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
