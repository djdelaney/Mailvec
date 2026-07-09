using System.Runtime.Versioning;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Mailvec.Mcp.Tests.Tools;

[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public class ViewAttachmentToolTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _maildirRoot;
    private readonly string _downloadDir;

    public ViewAttachmentToolTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mailvec-mcp-attach-tests-" + Guid.NewGuid().ToString("N"));
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

    private ViewAttachmentTool Build(TempDatabase db)
    {
        var ingest = Microsoft.Extensions.Options.Options.Create(new IngestOptions { MaildirRoot = _maildirRoot });
        var mcp = Microsoft.Extensions.Options.Options.Create(new McpOptions
        {
            AttachmentDownloadDir = _downloadDir,
        });
        var extractor = new AttachmentExtractor(ingest, mcp);
        return new ViewAttachmentTool(new MessageRepository(db.Connections), extractor, Helpers.NoopLogger());
    }

    private const string PdfMessage = """
        Message-ID: <attach-001@example.com>
        From: carol@example.com
        To: alice@example.com
        Subject: Quote attached
        MIME-Version: 1.0
        Content-Type: multipart/mixed; boundary="outer"

        --outer
        Content-Type: text/plain; charset=utf-8

        See attached.
        --outer
        Content-Type: application/pdf; name="quote.pdf"
        Content-Disposition: attachment; filename="quote.pdf"
        Content-Transfer-Encoding: base64

        JVBERi0xLjAKJSVFT0YK
        --outer--
        """;

    private long StagePdfMessage(MessageRepository repo, string fileName = "1.eml", string id = "attach-001@example.com")
    {
        var path = Path.Combine(_maildirRoot, "INBOX", "cur", fileName);
        File.WriteAllText(path, PdfMessage);

        var parsed = Helpers.Sample(id, attachments: [
            new ParsedAttachment(0, "quote.pdf", "application/pdf", 30L)
        ]);
        return repo.Upsert(parsed, "INBOX", "INBOX/cur", fileName, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Throws_when_neither_id_nor_messageId_provided()
    {
        using var db = new TempDatabase();
        Should.Throw<McpException>(() => Build(db).ViewAttachment(partIndex: 0));
    }

    [Fact]
    public void Throws_when_both_id_and_messageId_provided()
    {
        using var db = new TempDatabase();
        var ex = Should.Throw<McpException>(() => Build(db).ViewAttachment(partIndex: 0, id: 1, messageId: "x@y"));
        ex.Message.ShouldContain("OR");
    }

    [Fact]
    public void Throws_when_message_does_not_exist()
    {
        using var db = new TempDatabase();
        Should.Throw<McpException>(() => Build(db).ViewAttachment(partIndex: 0, id: 999));
    }

    [Fact]
    public void Throws_when_message_is_soft_deleted()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = StagePdfMessage(repo);
        repo.MarkDeleted([id], DateTimeOffset.UtcNow);

        var ex = Should.Throw<McpException>(() => Build(db).ViewAttachment(partIndex: 0, id: id));
        ex.Message.ShouldContain("soft-deleted");
    }

    [Fact]
    public void Throws_when_message_has_no_attachments()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = repo.Upsert(Helpers.Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var ex = Should.Throw<McpException>(() => Build(db).ViewAttachment(partIndex: 0, id: id));
        ex.Message.ShouldContain("no attachments");
    }

    [Fact]
    public void Out_of_range_part_index_is_translated_to_McpException()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = StagePdfMessage(repo);

        // PDF message has exactly one attachment (partIndex 0); 5 is out of range.
        var ex = Should.Throw<McpException>(() => Build(db).ViewAttachment(partIndex: 5, id: id));
        ex.Message.ShouldContain("out of range");
    }

    [Fact]
    public void Missing_maildir_file_is_translated_to_McpException()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        // Upsert claims attachments but never stage the .eml on disk.
        long id = repo.Upsert(
            Helpers.Sample("ghost@x", attachments: [new ParsedAttachment(0, "foo.pdf", "application/pdf", 100L)]),
            "INBOX", "INBOX/cur", "ghost.eml", DateTimeOffset.UtcNow);

        var ex = Should.Throw<McpException>(() => Build(db).ViewAttachment(partIndex: 0, id: id));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public void Non_inlineable_attachment_returns_summary_and_writes_nothing_to_disk()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = StagePdfMessage(repo);

        var result = Build(db).ViewAttachment(partIndex: 0, id: id);

        result.Content.ShouldNotBeEmpty();
        var summary = result.Content[0].ShouldBeOfType<TextContentBlock>();
        summary.Text.ShouldContain("quote.pdf");
        // A PDF can't be shown inline, so the summary points at the reader tools.
        summary.Text.ShouldContain("get_attachment_text");
        // No image/text blocks for a PDF — just the summary.
        result.Content.OfType<ImageContentBlock>().ShouldBeEmpty();

        // The point of the in-memory rework: no mail content is persisted to disk.
        Directory.GetFiles(_downloadDir).ShouldBeEmpty();
    }

    [Fact]
    public void Looking_up_by_messageId_works_identically_to_lookup_by_id()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        StagePdfMessage(repo);

        var result = Build(db).ViewAttachment(partIndex: 0, messageId: "attach-001@example.com");

        result.Content.ShouldNotBeEmpty();
        result.Content[0].ShouldBeOfType<TextContentBlock>().Text.ShouldContain("quote.pdf");
    }

    private const string CsvMessage = """
        Message-ID: <csv-001@example.com>
        From: carol@example.com
        To: alice@example.com
        Subject: Spreadsheet attached
        MIME-Version: 1.0
        Content-Type: multipart/mixed; boundary="outer"

        --outer
        Content-Type: text/plain; charset=utf-8

        See attached.
        --outer
        Content-Type: text/csv; name="data.csv"
        Content-Disposition: attachment; filename="data.csv"

        col_a,col_b
        1,2
        3,4

        --outer--
        """;

    [Fact]
    public void Text_attachment_under_inline_threshold_is_returned_as_extra_text_block()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var path = Path.Combine(_maildirRoot, "INBOX", "cur", "2.eml");
        File.WriteAllText(path, CsvMessage);
        repo.Upsert(
            Helpers.Sample("csv-001@example.com", attachments: [new ParsedAttachment(0, "data.csv", "text/csv", 30L)]),
            "INBOX", "INBOX/cur", "2.eml", DateTimeOffset.UtcNow);

        var result = Build(db).ViewAttachment(partIndex: 0, id: 1);

        // Summary + decoded UTF-8 contents = 2 blocks.
        result.Content.Count.ShouldBeGreaterThanOrEqualTo(2);
        var inline = result.Content[1].ShouldBeOfType<TextContentBlock>();
        inline.Text.ShouldContain("col_a,col_b");
        Directory.GetFiles(_downloadDir).ShouldBeEmpty();
    }

    // 1x1 transparent PNG (smallest valid PNG); base64 is round-trippable.
    private const string PngMessage = """
        Message-ID: <png-001@example.com>
        From: carol@example.com
        To: alice@example.com
        Subject: Image attached
        MIME-Version: 1.0
        Content-Type: multipart/mixed; boundary="outer"

        --outer
        Content-Type: text/plain; charset=utf-8

        See attached.
        --outer
        Content-Type: image/png; name="pixel.png"
        Content-Disposition: attachment; filename="pixel.png"
        Content-Transfer-Encoding: base64

        iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=
        --outer--
        """;

    [Fact]
    public void Image_attachment_is_returned_inline_as_ImageContentBlock_with_base64_data()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var path = Path.Combine(_maildirRoot, "INBOX", "cur", "3.eml");
        File.WriteAllText(path, PngMessage);
        repo.Upsert(
            Helpers.Sample("png-001@example.com", attachments: [new ParsedAttachment(0, "pixel.png", "image/png", 70L)]),
            "INBOX", "INBOX/cur", "3.eml", DateTimeOffset.UtcNow);

        var result = Build(db).ViewAttachment(partIndex: 0, id: 1);

        // Summary + ImageContentBlock = 2+ blocks.
        result.Content.Count.ShouldBeGreaterThanOrEqualTo(2);
        var image = result.Content.OfType<ImageContentBlock>().ShouldHaveSingleItem();
        image.MimeType.ShouldBe("image/png");
        image.Data.Length.ShouldBeGreaterThan(0);
        Directory.GetFiles(_downloadDir).ShouldBeEmpty();
    }

    /// <summary>Builds an .eml with a single base64 image attachment from raw bytes.</summary>
    private static string ImageMessage(string messageId, string fileName, string contentType, byte[] bytes) => $"""
        Message-ID: <{messageId}>
        From: carol@example.com
        To: alice@example.com
        Subject: Image attached
        MIME-Version: 1.0
        Content-Type: multipart/mixed; boundary="outer"

        --outer
        Content-Type: text/plain; charset=utf-8

        See attached.
        --outer
        Content-Type: {contentType}; name="{fileName}"
        Content-Disposition: attachment; filename="{fileName}"
        Content-Transfer-Encoding: base64

        {Convert.ToBase64String(bytes)}
        --outer--
        """;

    private void StageImageMessage(MessageRepository repo, string emlName, string messageId, string fileName, string contentType, byte[] bytes)
    {
        File.WriteAllText(Path.Combine(_maildirRoot, "INBOX", "cur", emlName), ImageMessage(messageId, fileName, contentType, bytes));
        repo.Upsert(
            Helpers.Sample(messageId, attachments: [new ParsedAttachment(0, fileName, contentType, bytes.LongLength)]),
            "INBOX", "INBOX/cur", emlName, DateTimeOffset.UtcNow);
    }

    /// <summary>An incompressible (noise) PNG comfortably over the 1 MB pass-through cap.</summary>
    private static byte[] LargeNoisePng()
    {
        var rng = new Random(42);
        var info = new SkiaSharp.SKImageInfo(1200, 1200, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque);
        using var bmp = new SkiaSharp.SKBitmap(info);
        var pixels = new byte[info.BytesSize];
        rng.NextBytes(pixels);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void Oversize_image_is_reencoded_to_jpeg_instead_of_passed_through()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var png = LargeNoisePng();
        png.Length.ShouldBeGreaterThan(1024 * 1024); // sanity: over the pass-through cap
        StageImageMessage(repo, "big.eml", "big-png@example.com", "big.png", "image/png", png);

        var result = Build(db).ViewAttachment(partIndex: 0, id: 1);

        var image = result.Content.OfType<ImageContentBlock>().ShouldHaveSingleItem();
        image.MimeType.ShouldBe("image/jpeg");
        // Base64-of-JPEG payload must land well under the original's size.
        image.Data.Length.ShouldBeLessThan(png.Length);
        result.Content[0].ShouldBeOfType<TextContentBlock>().Text.ShouldContain("re-encoded");
    }

    [Fact]
    public void Non_native_image_format_is_transcoded_to_jpeg()
    {
        // Minimal valid 2x2 24bpp BMP — SkiaSharp decodes BMP, but Claude vision
        // doesn't accept image/bmp, so it must come back transcoded.
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var bmp = MinimalBmp2X2();
        StageImageMessage(repo, "bmp.eml", "bmp-001@example.com", "tiny.bmp", "image/bmp", bmp);

        var result = Build(db).ViewAttachment(partIndex: 0, id: 1);

        var image = result.Content.OfType<ImageContentBlock>().ShouldHaveSingleItem();
        image.MimeType.ShouldBe("image/jpeg");
        result.Content[0].ShouldBeOfType<TextContentBlock>().Text.ShouldContain("re-encoded");
    }

    [Fact]
    public void Undecodable_image_returns_summary_without_image_block()
    {
        // SVG is image/* by MIME but not a decodable raster (SkiaSharp has no
        // SVG codec) — inlining it as an image block would be rejected by the
        // client, so the tool must fall back to a summary.
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"10\" height=\"10\"/></svg>");
        StageImageMessage(repo, "svg.eml", "svg-001@example.com", "logo.svg", "image/svg+xml", svg);

        var result = Build(db).ViewAttachment(partIndex: 0, id: 1);

        result.Content.OfType<ImageContentBlock>().ShouldBeEmpty();
        var summary = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        summary.ShouldContain("can't be decoded");
        summary.ShouldContain("logo.svg");
    }

    [Fact]
    public void Inline_text_over_display_window_is_truncated_with_paging_pointer()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        // Under the 256 KB decode cap but over the 50k-char display window.
        var csv = System.Text.Encoding.UTF8.GetBytes("col\n" + new string('x', 80_000));
        StageImageMessage(repo, "bigcsv.eml", "bigcsv@example.com", "big.csv", "text/csv", csv);

        var result = Build(db).ViewAttachment(partIndex: 0, id: 1);

        var summary = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        summary.ShouldContain("50,000");
        summary.ShouldContain("get_attachment_text");
        var inline = result.Content[1].ShouldBeOfType<TextContentBlock>().Text;
        inline.Length.ShouldBe(GetAttachmentTextTool.DefaultMaxChars);
    }

    private static byte[] MinimalBmp2X2()
    {
        // 14-byte file header + 40-byte BITMAPINFOHEADER + 2 rows of (2 px * 3 B + 2 B pad).
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(14 + 40 + 16);   // file size
        w.Write(0);              // reserved
        w.Write(14 + 40);        // pixel data offset
        w.Write(40);             // header size
        w.Write(2); w.Write(2);  // width, height
        w.Write((short)1);       // planes
        w.Write((short)24);      // bpp
        w.Write(0);              // compression (BI_RGB)
        w.Write(16);             // image size
        w.Write(2835); w.Write(2835); // ppm
        w.Write(0); w.Write(0);  // palette
        w.Write(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0 });   // row 1 (BGR + pad)
        w.Write(new byte[] { 0, 0, 255, 255, 255, 255, 0, 0 }); // row 2
        return ms.ToArray();
    }
}
