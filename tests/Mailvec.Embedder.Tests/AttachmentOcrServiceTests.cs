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

    private AttachmentOcrService Build(IVisionClient vision) =>
        new(_messages,
            new MaildirAttachmentReader(Options.Create(new IngestOptions { MaildirRoot = _maildirRoot })),
            vision,
            Options.Create(new EmbedderOptions()),
            NullLogger<AttachmentOcrService>.Instance);

    private long StageNoTextPdf(string id, byte[] pdfBytes)
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
            Attachments: [new ParsedAttachment(0, "scan.pdf", "application/pdf", pdfBytes.LongLength,
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
