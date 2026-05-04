using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Mailvec.Mcp.Tests.Tools;

public class GetAttachmentToolTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _maildirRoot;
    private readonly string _downloadDir;

    public GetAttachmentToolTests()
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

    private GetAttachmentTool Build(TempDatabase db)
    {
        var ingest = Microsoft.Extensions.Options.Options.Create(new IngestOptions { MaildirRoot = _maildirRoot });
        var mcp = Microsoft.Extensions.Options.Options.Create(new McpOptions
        {
            AttachmentDownloadDir = _downloadDir,
        });
        var extractor = new AttachmentExtractor(ingest, mcp);
        return new GetAttachmentTool(new MessageRepository(db.Connections), extractor, Helpers.NoopLogger());
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
        Should.Throw<McpException>(() => Build(db).GetAttachment(partIndex: 0));
    }

    [Fact]
    public void Throws_when_both_id_and_messageId_provided()
    {
        using var db = new TempDatabase();
        var ex = Should.Throw<McpException>(() => Build(db).GetAttachment(partIndex: 0, id: 1, messageId: "x@y"));
        ex.Message.ShouldContain("OR");
    }

    [Fact]
    public void Throws_when_message_does_not_exist()
    {
        using var db = new TempDatabase();
        Should.Throw<McpException>(() => Build(db).GetAttachment(partIndex: 0, id: 999));
    }

    [Fact]
    public void Throws_when_message_is_soft_deleted()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = StagePdfMessage(repo);
        repo.MarkDeleted([id], DateTimeOffset.UtcNow);

        var ex = Should.Throw<McpException>(() => Build(db).GetAttachment(partIndex: 0, id: id));
        ex.Message.ShouldContain("soft-deleted");
    }

    [Fact]
    public void Throws_when_message_has_no_attachments()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = repo.Upsert(Helpers.Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var ex = Should.Throw<McpException>(() => Build(db).GetAttachment(partIndex: 0, id: id));
        ex.Message.ShouldContain("no attachments");
    }

    [Fact]
    public void Out_of_range_part_index_is_translated_to_McpException()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = StagePdfMessage(repo);

        // PDF message has exactly one attachment (partIndex 0); 5 is out of range.
        var ex = Should.Throw<McpException>(() => Build(db).GetAttachment(partIndex: 5, id: id));
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

        var ex = Should.Throw<McpException>(() => Build(db).GetAttachment(partIndex: 0, id: id));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public void Happy_path_returns_summary_text_block_and_writes_file()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = StagePdfMessage(repo);

        var result = Build(db).GetAttachment(partIndex: 0, id: id);

        result.Content.ShouldNotBeEmpty();
        var summary = result.Content[0].ShouldBeOfType<TextContentBlock>();
        summary.Text.ShouldContain("quote.pdf");
        summary.Text.ShouldContain(_downloadDir);

        Directory.GetFiles(_downloadDir).Length.ShouldBe(1);
    }

    [Fact]
    public void Reusing_an_existing_extraction_uses_already_saved_phrasing()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = StagePdfMessage(repo);

        var tool = Build(db);
        tool.GetAttachment(partIndex: 0, id: id);
        var second = tool.GetAttachment(partIndex: 0, id: id);

        var summary = second.Content[0].ShouldBeOfType<TextContentBlock>();
        summary.Text.ShouldContain("Already saved");
    }
}
