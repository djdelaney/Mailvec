using System.Runtime.Versioning;
using System.Text;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Core.Vision;
using Mailvec.Embedder.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Mailvec.Embedder.Tests;

/// <summary>
/// The scanned-PDF OCR pass end to end: render (real PDFium) → fake vision OCR
/// → write back + re-queue. A blank PDF renders fine; the fake vision client
/// returns canned text regardless of pixels, so we test the pipeline without a
/// real Ollama. Platform-gated because the renderer is native.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public class AttachmentOcrServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _maildirRoot;
    private readonly ConnectionFactory _connections;
    private readonly MessageRepository _messages;

    public AttachmentOcrServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mailvec-ocr-svc-" + Guid.NewGuid().ToString("N"));
        _maildirRoot = Path.Combine(_root, "Mail");
        Directory.CreateDirectory(Path.Combine(_maildirRoot, "INBOX", "cur"));
        _connections = new ConnectionFactory(Options.Create(new ArchiveOptions
        {
            DatabasePath = Path.Combine(_root, "archive.sqlite"),
        }));
        new SchemaMigrator(_connections, NullLogger<SchemaMigrator>.Instance).EnsureUpToDate();
        _messages = new MessageRepository(_connections);
    }

    public void Dispose()
    {
        using (var conn = _connections.Open())
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(conn);
        }
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private AttachmentOcrService Build(IVisionClient vision, EmbedderOptions? opts = null) =>
        new(_messages,
            new MaildirAttachmentReader(Options.Create(new IngestOptions { MaildirRoot = _maildirRoot })),
            vision,
            Options.Create(opts ?? new EmbedderOptions()),
            NullLogger<AttachmentOcrService>.Instance);

    // Image tests use a tiny byte gate so a small generated PNG is still selected
    // by the SQL candidate query (the default gate is 50KB).
    private static EmbedderOptions ImageGate => new() { ImageOcrMinBytes = 1 };

    // partIndex normally 0 (the .eml written here has one attachment part);
    // pass a higher value to fabricate a stale DB row whose part doesn't
    // exist on disk — the Maildir read then throws ArgumentOutOfRange.
    private long StageNoTextPdf(string id, byte[] pdfBytes, int partIndex = 0)
    {
        var b64 = Convert.ToBase64String(pdfBytes);
        var eml =
            "Message-ID: <" + id + ">\nFrom: a@x\nTo: b@x\nSubject: s\nMIME-Version: 1.0\n" +
            "Content-Type: multipart/mixed; boundary=\"outer\"\n\n" +
            "--outer\nContent-Type: text/plain; charset=utf-8\n\nbody\n" +
            "--outer\nContent-Type: application/pdf; name=\"scan.pdf\"\n" +
            "Content-Disposition: attachment; filename=\"scan.pdf\"\nContent-Transfer-Encoding: base64\n\n" +
            b64 + "\n--outer--\n";
        File.WriteAllText(Path.Combine(_maildirRoot, "INBOX", "cur", id + ".eml"), eml);

        var parsed = new ParsedMessage(
            MessageId: id, ThreadId: id, Subject: "s", FromAddress: "a@x", FromName: null,
            ToAddresses: [], CcAddresses: [], DateSent: DateTimeOffset.UtcNow, BodyText: "body",
            BodyHtml: null, RawHeaders: $"Message-ID: <{id}>\r\n", SizeBytes: 100, ContentHash: $"h-{id}",
            Attachments: [new ParsedAttachment(partIndex, "scan.pdf", "application/pdf", pdfBytes.LongLength,
                ExtractedText: null, ExtractionStatus: AttachmentTextExtractor.StatusNoText)]);
        return _messages.Upsert(parsed, "INBOX", "INBOX/cur", id + ".eml", DateTimeOffset.UtcNow);
    }

    private string? StatusOf(long messageId) => _messages.GetById(messageId)!.Attachments[0].ExtractionStatus;
    private string? TextOf(long messageId) => _messages.GetById(messageId)!.Attachments[0].ExtractedText;

    [Fact]
    public async Task Ocrs_a_scanned_pdf_and_writes_text_with_ocr_status()
    {
        long id = StageNoTextPdf("scan@x", MinimalPdf(1));

        var done = await Build(new FakeVision(available: true, ocr: _ => "RECOVERED TEXT")).ProcessBatchAsync(10, default);

        done.ShouldBe(1);
        TextOf(id).ShouldBe("RECOVERED TEXT");
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusOcr);
    }

    [Fact]
    public async Task Concatenates_text_across_pages()
    {
        long id = StageNoTextPdf("multi@x", MinimalPdf(3));

        await Build(new FakeVision(available: true, ocr: _ => "PAGE")).ProcessBatchAsync(10, default);

        TextOf(id).ShouldBe("PAGE\n\nPAGE\n\nPAGE");
    }

    [Fact]
    public async Task Skips_and_leaves_no_text_when_the_model_is_unavailable()
    {
        long id = StageNoTextPdf("scan@x", MinimalPdf(1));

        var done = await Build(new FakeVision(available: false, ocr: _ => "x")).ProcessBatchAsync(10, default);

        done.ShouldBe(0);
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusNoText); // untouched, retried later
    }

    [Fact]
    public async Task Marks_failed_when_the_pdf_cannot_be_opened()
    {
        long id = StageNoTextPdf("bad@x", Encoding.ASCII.GetBytes("this is not a pdf at all"));

        var done = await Build(new FakeVision(available: true, ocr: _ => "x")).ProcessBatchAsync(10, default);

        done.ShouldBe(0);
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusFailed); // poison PDF, not retried
    }

    // ── Image OCR pass (ProcessImageBatchAsync) ──────────────────────────────

    private long StageUnsupportedImage(string id, byte[] imageBytes, string contentType = "image/png")
    {
        var b64 = Convert.ToBase64String(imageBytes);
        var eml =
            "Message-ID: <" + id + ">\nFrom: a@x\nTo: b@x\nSubject: s\nMIME-Version: 1.0\n" +
            "Content-Type: multipart/mixed; boundary=\"outer\"\n\n" +
            "--outer\nContent-Type: text/plain; charset=utf-8\n\nbody\n" +
            "--outer\nContent-Type: " + contentType + "; name=\"photo.img\"\n" +
            "Content-Disposition: attachment; filename=\"photo.img\"\nContent-Transfer-Encoding: base64\n\n" +
            b64 + "\n--outer--\n";
        File.WriteAllText(Path.Combine(_maildirRoot, "INBOX", "cur", id + ".eml"), eml);

        var parsed = new ParsedMessage(
            MessageId: id, ThreadId: id, Subject: "s", FromAddress: "a@x", FromName: null,
            ToAddresses: [], CcAddresses: [], DateSent: DateTimeOffset.UtcNow, BodyText: "body",
            BodyHtml: null, RawHeaders: $"Message-ID: <{id}>\r\n", SizeBytes: 100, ContentHash: $"h-{id}",
            Attachments: [new ParsedAttachment(0, "photo.img", contentType, imageBytes.LongLength,
                ExtractedText: null, ExtractionStatus: AttachmentTextExtractor.StatusUnsupported)]);
        return _messages.Upsert(parsed, "INBOX", "INBOX/cur", id + ".eml", DateTimeOffset.UtcNow);
    }

    private static byte[] MakePng(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.CornflowerBlue);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task Ocrs_an_image_and_writes_text_with_ocr_status()
    {
        long id = StageUnsupportedImage("img@x", MakePng(300, 300));

        var done = await Build(new FakeVision(true, _ => "IMAGE TEXT"), ImageGate).ProcessImageBatchAsync(10, default);

        done.ShouldBe(1);
        TextOf(id).ShouldBe("IMAGE TEXT");
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusOcr);
    }

    [Fact]
    public async Task Image_model_unavailable_leaves_it_unsupported()
    {
        long id = StageUnsupportedImage("img@x", MakePng(300, 300));

        var done = await Build(new FakeVision(false, _ => "x"), ImageGate).ProcessImageBatchAsync(10, default);

        done.ShouldBe(0);
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusUnsupported); // retried later
    }

    [Fact]
    public async Task Marks_failed_when_the_image_cannot_be_decoded()
    {
        long id = StageUnsupportedImage("bad@x", Encoding.ASCII.GetBytes("this is not an image, just bytes past the tiny byte gate"));

        var done = await Build(new FakeVision(true, _ => "x"), ImageGate).ProcessImageBatchAsync(10, default);

        done.ShouldBe(0);
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusFailed); // undecodable, not retried
    }

    [Fact]
    public async Task Gates_out_a_too_small_image_as_no_text()
    {
        long id = StageUnsupportedImage("tiny@x", MakePng(100, 100)); // < 200px min dimension

        var done = await Build(new FakeVision(true, _ => "SHOULD NOT BE CALLED"), ImageGate).ProcessImageBatchAsync(10, default);

        done.ShouldBe(0);
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusNoText); // gated, not OCR'd
        TextOf(id).ShouldBeNull();
    }

    [Fact]
    public async Task Gates_out_an_extreme_aspect_image_as_no_text()
    {
        long id = StageUnsupportedImage("banner@x", MakePng(2000, 210)); // aspect 9.5 > 8

        var done = await Build(new FakeVision(true, _ => "x"), ImageGate).ProcessImageBatchAsync(10, default);

        done.ShouldBe(0);
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusNoText);
    }

    [Fact]
    public async Task Empty_transcription_marks_the_image_no_text()
    {
        long id = StageUnsupportedImage("blank@x", MakePng(300, 300));

        var done = await Build(new FakeVision(true, _ => "   "), ImageGate).ProcessImageBatchAsync(10, default);

        done.ShouldBe(0);
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusNoText); // no text found, not an empty 'ocr' row
    }

    [Fact]
    public async Task Vision_timeout_is_transient_leaving_the_image_for_retry()
    {
        long id = StageUnsupportedImage("slow@x", MakePng(300, 300));
        // An HTTP timeout surfaces as TaskCanceledException (an OperationCanceledException)
        // while the caller's token is NOT cancelled. It must be treated as transient —
        // batch aborts, image left 'unsupported', nothing thrown to the worker.
        var svc = Build(new FakeVision(true, _ => throw new TaskCanceledException("HttpClient.Timeout")), ImageGate);

        var done = await svc.ProcessImageBatchAsync(10, default);

        done.ShouldBe(0);
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusUnsupported); // NOT failed
    }

    [Fact]
    public async Task Wedged_ollama_never_retires_documents_no_matter_how_many_cycles()
    {
        // /api/tags answers 200 even when Ollama can't actually load the model
        // (GPU OOM, dead runner), so the availability probe passes while every
        // vision call times out. Those failures must NOT count toward poison-
        // document retirement — an hours-long wedge used to permanently mark
        // perfectly good scans 'failed', one head-of-queue doc per few cycles.
        long a = StageNoTextPdf("wedge-a@x", MinimalPdf(1));
        long b = StageNoTextPdf("wedge-b@x", MinimalPdf(1));
        var svc = Build(new FakeVision(true, _ => throw new TaskCanceledException("HttpClient.Timeout")));

        for (int cycle = 0; cycle < 8; cycle++) // well past MaxVisionAttempts
        {
            (await svc.ProcessBatchAsync(10, default)).ShouldBe(0);
        }

        StatusOf(a).ShouldBe(AttachmentTextExtractor.StatusNoText); // untouched, retried when Ollama recovers
        StatusOf(b).ShouldBe(AttachmentTextExtractor.StatusNoText);
    }

    [Fact]
    public async Task Poison_document_retires_after_repeated_failures_alongside_successes()
    {
        // The poison doc (lowest attachment id, so always selected first)
        // fails every cycle while other documents OCR fine — proof the model
        // is healthy, so its failures count and it retires after
        // MaxVisionAttempts cycles. Meanwhile the failure must not block the
        // documents behind it in the queue.
        long poison = StageNoTextPdf("poison@x", MinimalPdf(1));
        var calls = 0;
        // Candidates are ordered by attachment id: the poison doc's call is
        // always the first of each cycle (odd call numbers).
        var svc = Build(new FakeVision(true, _ =>
            ++calls % 2 == 1 ? throw new TaskCanceledException("poison render hangs the model") : "GOOD TEXT"));

        var healthy = new List<long>();
        for (int cycle = 0; cycle < 5; cycle++) // MaxVisionAttempts
        {
            healthy.Add(StageNoTextPdf($"fresh-{cycle}@x", MinimalPdf(1)));
            await svc.ProcessBatchAsync(10, default);
        }

        // Head-of-line liveness: every healthy doc behind the poison one got
        // OCR'd in its own cycle.
        foreach (var id in healthy)
        {
            StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusOcr);
            TextOf(id).ShouldBe("GOOD TEXT");
        }
        // And the poison doc is retired so it stops costing a timeout per cycle.
        StatusOf(poison).ShouldBe(AttachmentTextExtractor.StatusFailed);
    }

    [Fact]
    public async Task Unreadable_candidate_is_retired_and_does_not_block_the_batch()
    {
        // A DB row whose part_index doesn't exist in the .eml (stale row after
        // a post-ingest rewrite) throws from the Maildir read. Before the
        // tiered catch, that exception escaped ProcessBatchAsync entirely —
        // aborting BOTH OCR passes for the cycle — and the id-ordered
        // candidate query re-selected the same row first every cycle: a
        // permanent, silent stall of the whole OCR queue.
        long poison = StageNoTextPdf("stale-part@x", MinimalPdf(1), partIndex: 7);
        long healthy = StageNoTextPdf("fine@x", MinimalPdf(1));
        var svc = Build(new FakeVision(true, _ => "GOOD TEXT"));

        var done = await svc.ProcessBatchAsync(10, default);

        done.ShouldBe(1);
        StatusOf(poison).ShouldBe(AttachmentTextExtractor.StatusFailed); // retired immediately, not retried
        StatusOf(healthy).ShouldBe(AttachmentTextExtractor.StatusOcr);   // the queue behind it still drains
        TextOf(healthy).ShouldBe("GOOD TEXT");
    }

    [Fact]
    public async Task Real_cancellation_propagates()
    {
        long id = StageUnsupportedImage("cancel@x", MakePng(300, 300));
        using var cts = new CancellationTokenSource();
        // Cancel mid-call, then throw OCE: with the token cancelled this is a real
        // shutdown and must propagate (not be swallowed as transient).
        var svc = Build(new FakeVision(true, _ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); }), ImageGate);

        await Should.ThrowAsync<OperationCanceledException>(() => svc.ProcessImageBatchAsync(10, cts.Token));
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusUnsupported);
    }

    private sealed class FakeVision(bool available, Func<byte[], string> ocr) : IVisionClient
    {
        public Task<string> OcrAsync(byte[] image, CancellationToken ct = default) => Task.FromResult(ocr(image));
        public Task<string> OcrImageAsync(byte[] image, CancellationToken ct = default) => Task.FromResult(ocr(image));
        public Task<bool> IsModelAvailableAsync(CancellationToken ct = default) => Task.FromResult(available);
    }

    /// <summary>Minimal valid PDF with <paramref name="pages"/> blank pages (xref offsets computed).</summary>
    private static byte[] MinimalPdf(int pages)
    {
        var objects = new List<string>
        {
            "<</Type /Catalog /Pages 2 0 R>>",
            $"<</Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{3 + i} 0 R"))}] /Count {pages}>>",
        };
        for (int i = 0; i < pages; i++)
            objects.Add("<</Type /Page /Parent 2 0 R /MediaBox [0 0 200 200]>>");

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n").Append($"0 {objects.Count + 1}\n").Append("0000000000 65535 f \n");
        foreach (var off in offsets)
            sb.Append(off.ToString("D10") + " 00000 n \n");
        sb.Append($"trailer\n<</Size {objects.Count + 1} /Root 1 0 R>>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
