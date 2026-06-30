using Mailvec.Core.Data;
using Mailvec.Core.Parsing;
using Mailvec.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Mailvec.Mcp.Tests.Tools;

/// <summary>
/// get_attachment_text is a pure DB read (no Maildir, no download dir), so the
/// tests just seed attachments.extracted_text via Upsert and assert the
/// returned content blocks. Covers the done path, each not-extractable status,
/// and the id/messageId/partIndex guards shared with the other tools.
/// </summary>
public class GetAttachmentTextToolTests
{
    private static GetAttachmentTextTool Build(TempDatabase db) =>
        new(new MessageRepository(db.Connections), Helpers.NoopLogger());

    private static long Seed(MessageRepository repo, string id, string? text, string? status, string fileName = "report.pdf")
    {
        var parsed = Helpers.Sample(id, attachments: [
            new ParsedAttachment(0, fileName, "application/pdf", 1234L, ExtractedText: text, ExtractionStatus: status),
        ]);
        return repo.Upsert(parsed, "INBOX", "INBOX/cur", id + ".eml", DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Throws_when_neither_id_nor_messageId_provided()
    {
        using var db = new TempDatabase();
        Should.Throw<McpException>(() => Build(db).GetAttachmentText(partIndex: 0));
    }

    [Fact]
    public void Throws_when_both_id_and_messageId_provided()
    {
        using var db = new TempDatabase();
        var ex = Should.Throw<McpException>(() => Build(db).GetAttachmentText(partIndex: 0, id: 1, messageId: "x@y"));
        ex.Message.ShouldContain("OR");
    }

    [Fact]
    public void Throws_when_message_does_not_exist()
    {
        using var db = new TempDatabase();
        Should.Throw<McpException>(() => Build(db).GetAttachmentText(partIndex: 0, id: 999));
    }

    [Fact]
    public void Throws_when_message_is_soft_deleted()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "del@x", "some text", "done");
        repo.MarkDeleted([id], DateTimeOffset.UtcNow);

        var ex = Should.Throw<McpException>(() => Build(db).GetAttachmentText(partIndex: 0, id: id));
        ex.Message.ShouldContain("soft-deleted");
    }

    [Fact]
    public void Throws_when_message_has_no_attachments()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = repo.Upsert(Helpers.Sample("none@x"), "INBOX", "INBOX/cur", "none.eml", DateTimeOffset.UtcNow);

        var ex = Should.Throw<McpException>(() => Build(db).GetAttachmentText(partIndex: 0, id: id));
        ex.Message.ShouldContain("no attachments");
    }

    [Fact]
    public void Throws_when_part_index_not_present()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "one@x", "text", "done");

        var ex = Should.Throw<McpException>(() => Build(db).GetAttachmentText(partIndex: 5, id: id));
        ex.Message.ShouldContain("partIndex 5");
    }

    [Fact]
    public void Done_status_returns_summary_then_extracted_text()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "done@x", "Invoice total: $1,234.56\nDue 2026-07-15", "done");

        var result = Build(db).GetAttachmentText(partIndex: 0, id: id);

        result.Content.Count.ShouldBe(2);
        var summary = result.Content[0].ShouldBeOfType<TextContentBlock>();
        summary.Text.ShouldContain("report.pdf");
        summary.Text.ShouldContain("partIndex 0");

        var body = result.Content[1].ShouldBeOfType<TextContentBlock>();
        body.Text.ShouldContain("Invoice total: $1,234.56");
    }

    [Fact]
    public void Lookup_by_messageId_works_identically()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        Seed(repo, "bymid@x", "hello from the pdf", "done");

        var result = Build(db).GetAttachmentText(partIndex: 0, messageId: "bymid@x");

        result.Content[1].ShouldBeOfType<TextContentBlock>().Text.ShouldContain("hello from the pdf");
    }

    [Fact]
    public void No_text_status_explains_scanned_pdf_and_points_at_get_attachment()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "scan@x", text: null, status: "no_text", fileName: "scan.pdf");

        var result = Build(db).GetAttachmentText(partIndex: 0, id: id);

        result.Content.Count.ShouldBe(1);
        var msg = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        msg.ShouldContain("scan.pdf");
        msg.ShouldContain("get_attachment");
        msg.ShouldContain("scanned");
    }

    [Fact]
    public void Encrypted_status_is_reported()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "enc@x", text: null, status: "encrypted");

        var msg = Build(db).GetAttachmentText(partIndex: 0, id: id).Content[0].ShouldBeOfType<TextContentBlock>().Text;
        msg.ShouldContain("encrypted");
    }

    [Fact]
    public void Null_status_reports_no_extraction_record()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Seed(repo, "legacy@x", text: null, status: null);

        var msg = Build(db).GetAttachmentText(partIndex: 0, id: id).Content[0].ShouldBeOfType<TextContentBlock>().Text;
        msg.ShouldContain("get_attachment");
    }
}
