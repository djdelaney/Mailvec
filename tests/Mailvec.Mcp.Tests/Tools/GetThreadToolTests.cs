using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Mcp.Tools;
using ModelContextProtocol;

namespace Mailvec.Mcp.Tests.Tools;

public class GetThreadToolTests
{
    private static GetThreadTool Build(TempDatabase db, FastmailOptions? fastmail = null) =>
        new(new MessageRepository(db.Connections),
            Helpers.Fastmail(fastmail),
            Helpers.NoopLogger());

    [Fact]
    public void Throws_when_neither_id_nor_messageId_provided()
    {
        using var db = new TempDatabase();
        Should.Throw<McpException>(() => Build(db).GetThread(id: null, messageId: null));
    }

    [Fact]
    public void Throws_when_both_provided()
    {
        using var db = new TempDatabase();
        var ex = Should.Throw<McpException>(() => Build(db).GetThread(id: 1, messageId: "x@y"));
        ex.Message.ShouldContain("OR");
    }

    [Fact]
    public void Throws_when_message_does_not_exist()
    {
        using var db = new TempDatabase();
        var ex = Should.Throw<McpException>(() => Build(db).GetThread(messageId: "ghost@x"));
        ex.Message.ShouldContain("ghost@x");
    }

    [Fact]
    public void Returns_full_thread_in_chronological_order()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var t = "thread-1";
        var d1 = new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var d2 = new DateTimeOffset(2024, 6, 1, 11, 0, 0, TimeSpan.Zero);
        var d3 = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

        repo.Upsert(Helpers.Sample("c@x", threadId: t, dateSent: d3), "INBOX", "INBOX/cur", "c", DateTimeOffset.UtcNow);
        repo.Upsert(Helpers.Sample("a@x", threadId: t, dateSent: d1), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);
        repo.Upsert(Helpers.Sample("b@x", threadId: t, dateSent: d2), "INBOX", "INBOX/cur", "b", DateTimeOffset.UtcNow);

        var resp = Build(db).GetThread(messageId: "b@x");

        resp.ThreadId.ShouldBe(t);
        resp.Count.ShouldBe(3);
        resp.Messages.Select(m => m.MessageId).ShouldBe(new[] { "a@x", "b@x", "c@x" });
    }

    [Fact]
    public void Bodies_are_omitted_by_default_but_snippets_present()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        repo.Upsert(Helpers.Sample("a@x", threadId: "t", body: "hello world"),
            "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var resp = Build(db).GetThread(messageId: "a@x");

        resp.Messages[0].BodyText.ShouldBeNull();
        resp.Messages[0].Snippet.ShouldContain("hello");
    }

    [Fact]
    public void IncludeBodies_returns_full_body_text_per_message()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        repo.Upsert(Helpers.Sample("a@x", threadId: "t", body: "hello world"),
            "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var resp = Build(db).GetThread(messageId: "a@x", includeBodies: true);

        resp.Messages[0].BodyText.ShouldBe("hello world");
    }

    [Fact]
    public void Lone_message_with_null_thread_id_returns_singleton_not_empty()
    {
        // CLAUDE.md gotcha: GetThreadByMessageId returns just the message when
        // thread_id is NULL (singletons are common — notifications, marketing).
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        repo.Upsert(Helpers.Sample("solo@x"), "INBOX", "INBOX/cur", "s", DateTimeOffset.UtcNow);
        // ParsedMessage.ThreadId is non-nullable; set the column to NULL out of
        // band to simulate a parsed-with-no-references singleton.
        using (var conn = db.Connections.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE messages SET thread_id = NULL WHERE message_id = 'solo@x'";
            cmd.ExecuteNonQuery();
        }

        var resp = Build(db).GetThread(messageId: "solo@x");

        resp.Count.ShouldBe(1);
        resp.Messages.Single().MessageId.ShouldBe("solo@x");
        resp.ThreadId.ShouldBeNull();
    }

    [Fact]
    public void Snippet_truncates_at_200_chars_with_ellipsis()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var longBody = new string('x', 500);
        repo.Upsert(Helpers.Sample("a@x", threadId: "t", body: longBody), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var snippet = Build(db).GetThread(messageId: "a@x").Messages[0].Snippet;

        snippet.Length.ShouldBeLessThanOrEqualTo(201);
        snippet.ShouldEndWith("…");
    }

    [Fact]
    public void Webmail_url_emitted_when_AccountId_set()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        repo.Upsert(Helpers.Sample("a@x", threadId: "t"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var resp = Build(db, new FastmailOptions { AccountId = "u1" }).GetThread(messageId: "a@x");

        resp.Messages[0].WebmailUrl.ShouldNotBeNull();
    }
}
