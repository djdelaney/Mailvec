using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Health;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Core.Tests.Data;
using Mailvec.Core.Vision;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Tests.Health;

/// <summary>
/// The OCR block of <see cref="HealthService.CheckAsync"/>: the stage is enabled
/// when *either* OCR pass is on, a disabled pass's pending is zeroed (so /health
/// never shows a backlog that will never drain), and recovered totals are shown
/// regardless. Complements the wire-shape coverage in TrayStatusServiceTests.
/// </summary>
public class HealthServiceOcrTests
{
    private static void Insert(MessageRepository repo, string id, string status, string contentType, long size)
    {
        // The PDF-pending predicate keys off a .pdf filename; images key off
        // content_type — give each a name that matches its OcrCounts leg.
        var fileName = contentType == "application/pdf" ? "scan.pdf" : "photo.png";
        var parsed = new ParsedMessage(
            MessageId: id, ThreadId: id, Subject: "s", FromAddress: "a@x", FromName: null,
            ToAddresses: [], CcAddresses: [], DateSent: DateTimeOffset.UtcNow, BodyText: "body",
            BodyHtml: null, RawHeaders: $"Message-ID: <{id}>\r\n", SizeBytes: 100, ContentHash: $"h-{id}",
            Attachments: [new ParsedAttachment(0, fileName, contentType, size, ExtractedText: null, ExtractionStatus: status)]);
        repo.Upsert(parsed, "INBOX", "INBOX/cur", id + ".eml", DateTimeOffset.UtcNow);
    }

    private static HealthService Build(TempDatabase db, EmbedderOptions emb) =>
        new(db.Connections,
            new MetadataRepository(db.Connections),
            new FakeEmbedding(),
            Microsoft.Extensions.Options.Options.Create(new ArchiveOptions { DatabasePath = db.DatabasePath }),
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions()),
            new MessageRepository(db.Connections),
            new FakeVision(available: true),
            Microsoft.Extensions.Options.Options.Create(emb));

    [Fact]
    public async Task Ocr_disabled_reports_disabled_and_zero_pending_but_keeps_recovered()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        Insert(repo, "pdfq@x", AttachmentTextExtractor.StatusNoText, "application/pdf", 60000);   // would be pending
        Insert(repo, "imgrec@x", AttachmentTextExtractor.StatusOcr, "image/png", 60000);          // already recovered

        var r = await Build(db, new EmbedderOptions { OcrEnabled = false, ImageOcrEnabled = false }).CheckAsync();

        r.Ocr.Enabled.ShouldBeFalse();
        r.Ocr.Pending.ShouldBe(0);            // both passes off → nothing counts as pending
        r.Ocr.Recovered.ShouldBe(1);          // historical, shown regardless of enable state
        r.Ocr.ImageRecovered.ShouldBe(1);
    }

    [Fact]
    public async Task Only_image_ocr_enabled_counts_image_pending_not_pdf()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        Insert(repo, "pdfq@x", AttachmentTextExtractor.StatusNoText, "application/pdf", 60000);
        Insert(repo, "imgq@x", AttachmentTextExtractor.StatusUnsupported, "image/png", 60000);

        var r = await Build(db, new EmbedderOptions { OcrEnabled = false, ImageOcrEnabled = true }).CheckAsync();

        r.Ocr.Enabled.ShouldBeTrue();         // either pass on → stage is enabled
        r.Ocr.ImagePending.ShouldBe(1);
        r.Ocr.Pending.ShouldBe(1);            // PDF pending zeroed because its pass is off
    }

    [Fact]
    public async Task Both_enabled_counts_the_full_pending_split()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        Insert(repo, "pdfq@x", AttachmentTextExtractor.StatusNoText, "application/pdf", 60000);
        Insert(repo, "imgq@x", AttachmentTextExtractor.StatusUnsupported, "image/png", 60000);

        var r = await Build(db, new EmbedderOptions { OcrEnabled = true, ImageOcrEnabled = true }).CheckAsync();

        r.Ocr.Pending.ShouldBe(2);            // pdf + image
        r.Ocr.ImagePending.ShouldBe(1);
    }

    private sealed class FakeEmbedding : IEmbeddingClient
    {
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<float[]>());
        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakeVision(bool available) : IVisionClient
    {
        public Task<string> OcrAsync(byte[] image, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> OcrImageAsync(byte[] image, CancellationToken ct = default) => Task.FromResult("");
        public Task<bool> IsModelAvailableAsync(CancellationToken ct = default) => Task.FromResult(available);
    }
}
