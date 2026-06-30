using System.Runtime.Versioning;
using System.Text;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Mcp.Tools;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Mailvec.Mcp.Tests.Tools;

/// <summary>
/// Exercises the PDF page-image tool end to end, including a real PDFium
/// render — which doubles as the "native lib loads on this RID" check. PDFs
/// are generated in-memory with correct xref offsets so PDFium parses them
/// cleanly.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public class GetAttachmentPageImageToolTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _maildirRoot;
    private readonly string _downloadDir;

    public GetAttachmentPageImageToolTests()
    {
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

    // ---------- direct renderer (native-load smoke test) ----------

    [Fact]
    public void PdfRenderer_reports_page_count_and_emits_a_png()
    {
        var pdf = MinimalPdf(pages: 2);

        PdfRenderer.PageCount(pdf).ShouldBe(2);

        var png = PdfRenderer.RenderPagePng(pdf, pageIndex: 0);
        png.Length.ShouldBeGreaterThan(0);
        // PNG signature: 89 50 4E 47.
        png[0].ShouldBe((byte)0x89);
        png[1].ShouldBe((byte)0x50);
        png[2].ShouldBe((byte)0x4E);
        png[3].ShouldBe((byte)0x47);
    }

    // ---------- tool guards ----------

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
        long id = StagePdf(repo, pages: 1);

        var ex = Should.Throw<McpException>(() => Build(db).GetAttachmentPageImage(partIndex: 0, page: 2, id: id));
        ex.Message.ShouldContain("out of range");
    }

    // ---------- happy path (real render) ----------

    [Fact]
    public void Renders_first_page_to_an_image_block()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = StagePdf(repo, pages: 1);

        var result = Build(db).GetAttachmentPageImage(partIndex: 0, page: 1, id: id);

        var summary = result.Content[0].ShouldBeOfType<TextContentBlock>();
        summary.Text.ShouldContain("page 1 of 1");
        summary.Text.ShouldContain("doc.pdf");

        var image = result.Content.OfType<ImageContentBlock>().ShouldHaveSingleItem();
        image.MimeType.ShouldBe("image/png");
        image.Data.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Renders_the_requested_page_of_a_multipage_pdf()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = StagePdf(repo, pages: 3);

        var result = Build(db).GetAttachmentPageImage(partIndex: 0, page: 3, id: id);

        result.Content[0].ShouldBeOfType<TextContentBlock>().Text.ShouldContain("page 3 of 3");
        result.Content.OfType<ImageContentBlock>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Lookup_by_messageId_works_identically()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        StagePdf(repo, pages: 1, id: "bymid@x", file: "bymid.eml");

        var result = Build(db).GetAttachmentPageImage(partIndex: 0, messageId: "bymid@x");

        result.Content.OfType<ImageContentBlock>().ShouldHaveSingleItem();
    }

    // ---------- staging helpers ----------

    private long StagePdf(MessageRepository repo, int pages, string id = "pdf@x", string file = "p.eml")
    {
        var pdf = MinimalPdf(pages);
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

    /// <summary>
    /// Build a minimal valid PDF with <paramref name="pages"/> blank pages,
    /// computing xref byte offsets from actual positions so PDFium parses it
    /// without falling back to repair. All-ASCII, so string length == byte offset.
    /// </summary>
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
