using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Mcp.Tools;
using ModelContextProtocol;

namespace Mailvec.Mcp.Tests.Tools;

public class GetEmailToolTests
{
    private static GetEmailTool Build(TempDatabase db, FastmailOptions? fastmail = null) =>
        new(new MessageRepository(db.Connections),
            Helpers.Fastmail(fastmail),
            Helpers.NoopLogger());

    [Fact]
    public void Throws_when_neither_id_nor_messageId_provided()
    {
        using var db = new TempDatabase();
        var tool = Build(db);

        Should.Throw<McpException>(() => tool.GetEmail(id: null, messageId: null));
    }

    [Fact]
    public void Throws_when_both_id_and_messageId_provided()
    {
        using var db = new TempDatabase();
        var tool = Build(db);

        var ex = Should.Throw<McpException>(() => tool.GetEmail(id: 1, messageId: "x@y"));
        ex.Message.ShouldContain("OR");
    }

    [Fact]
    public void Throws_when_id_does_not_exist()
    {
        using var db = new TempDatabase();
        var tool = Build(db);

        var ex = Should.Throw<McpException>(() => tool.GetEmail(id: 999));
        ex.Message.ShouldContain("999");
    }

    [Fact]
    public void Throws_when_messageId_does_not_exist()
    {
        using var db = new TempDatabase();
        var tool = Build(db);

        var ex = Should.Throw<McpException>(() => tool.GetEmail(messageId: "ghost@nowhere"));
        ex.Message.ShouldContain("ghost@nowhere");
    }

    [Fact]
    public void Throws_when_message_is_soft_deleted()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = repo.Upsert(Helpers.Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);
        repo.MarkDeleted([id], DateTimeOffset.UtcNow);

        var tool = Build(db);
        var ex = Should.Throw<McpException>(() => tool.GetEmail(id: id));
        ex.Message.ShouldContain("soft-deleted");
    }

    [Fact]
    public void Returns_message_by_id_with_text_body_only_by_default()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = repo.Upsert(
            Helpers.Sample("a@x", subject: "Hi", body: "hello world") with { BodyHtml = "<p>hello world</p>" },
            "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var resp = Build(db).GetEmail(id: id);

        resp.Id.ShouldBe(id);
        resp.MessageId.ShouldBe("a@x");
        resp.Subject.ShouldBe("Hi");
        resp.BodyText.ShouldBe("hello world");
        resp.BodyHtml.ShouldBeNull();   // includeHtml defaults to false
    }

    [Fact]
    public void IncludeHtml_returns_html_body()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = repo.Upsert(
            Helpers.Sample("a@x", body: "text") with { BodyHtml = "<p>html</p>" },
            "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var resp = Build(db).GetEmail(id: id, includeHtml: true);

        resp.BodyHtml.ShouldBe("<p>html</p>");
    }

    [Fact]
    public void Lookup_by_messageId_works_alongside_lookup_by_id()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        repo.Upsert(Helpers.Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var resp = Build(db).GetEmail(messageId: "a@x");

        resp.MessageId.ShouldBe("a@x");
    }

    [Fact]
    public void Returns_per_attachment_metadata()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var atts = new[]
        {
            new ParsedAttachment(0, "report.pdf", "application/pdf", 1234L),
            new ParsedAttachment(1, "image.png",  "image/png",       5678L),
        };
        long id = repo.Upsert(Helpers.Sample("a@x", attachments: atts), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var resp = Build(db).GetEmail(id: id);

        resp.Attachments.Count.ShouldBe(2);
        resp.Attachments[0].PartIndex.ShouldBe(0);
        resp.Attachments[0].FileName.ShouldBe("report.pdf");
        resp.Attachments[0].ContentType.ShouldBe("application/pdf");
        resp.Attachments[1].FileName.ShouldBe("image.png");
    }

    [Fact]
    public void Webmail_url_emitted_when_AccountId_set()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = repo.Upsert(Helpers.Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var resp = Build(db, new FastmailOptions { AccountId = "u1" }).GetEmail(id: id);

        resp.WebmailUrl.ShouldNotBeNull();
        resp.WebmailUrl.ShouldContain("u1");
    }
}
