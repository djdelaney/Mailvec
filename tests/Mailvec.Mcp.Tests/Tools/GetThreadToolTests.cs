using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Mcp.Tools;
using ModelContextProtocol;

namespace Mailvec.Mcp.Tests.Tools;

public class GetThreadToolTests
{
    private static GetThreadTool Build(TempDatabase db, FastmailOptions? fastmail = null, McpOptions? mcp = null) =>
        new(new MessageRepository(db.Connections),
            Helpers.Fastmail(fastmail),
            Helpers.Mcp(mcp),
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
    public void Entries_list_attachments_with_get_email_shape()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var t = "thread-att";
        var d1 = new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var d2 = new DateTimeOffset(2024, 6, 1, 11, 0, 0, TimeSpan.Zero);

        repo.Upsert(Helpers.Sample("plain@x", threadId: t, dateSent: d1), "INBOX", "INBOX/cur", "p", DateTimeOffset.UtcNow);
        repo.Upsert(Helpers.Sample("invoice@x", threadId: t, dateSent: d2, attachments: [
            new Mailvec.Core.Parsing.ParsedAttachment(0, "invoice.pdf", "application/pdf", 1234L,
                ExtractedText: "Total due: $42", ExtractionStatus: "done"),
        ]), "INBOX", "INBOX/cur", "i", DateTimeOffset.UtcNow);

        var resp = Build(db).GetThread(messageId: "plain@x");

        resp.Messages.Count.ShouldBe(2);
        resp.Messages[0].Attachments.ShouldBeEmpty();

        var att = resp.Messages[1].Attachments.ShouldHaveSingleItem();
        att.PartIndex.ShouldBe(0);
        att.FileName.ShouldBe("invoice.pdf");
        att.ExtractionStatus.ShouldBe("done");
        att.IndexedForSearch.ShouldBeTrue();
        att.ExtractedTextChars.ShouldBe("Total due: $42".Length);
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

    // ---------- response bounds ----------
    //
    // A thread's size is chosen by whoever replied to it, not by the caller —
    // every other mail-bearing tool takes its bound as an argument. These pin
    // that the caps exist, that truncation is REPORTED rather than silent (a
    // model that can't tell it saw half a thread will summarise it as if it saw
    // all of it), and that Count keeps meaning "entries in Messages".

    [Fact]
    public void Long_threads_are_clipped_to_the_message_cap_and_report_it()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var start = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 10; i++)
        {
            repo.Upsert(Helpers.Sample($"m{i}@x", threadId: "big", dateSent: start.AddMinutes(i)),
                "INBOX", "INBOX/cur", $"m{i}", DateTimeOffset.UtcNow);
        }

        var resp = Build(db, mcp: new McpOptions { ThreadMaxMessages = 4 }).GetThread(messageId: "m0@x");

        resp.Count.ShouldBe(4);
        resp.Messages.Count.ShouldBe(4);   // Count == Messages.Count, always
        resp.TotalCount.ShouldBe(10);      // the honest thread size
        resp.Truncated.ShouldBeTrue();
        // Chronological prefix, so the documented "oldest first" ordering still
        // describes what came back.
        resp.Messages.Select(m => m.MessageId).ShouldBe(new[] { "m0@x", "m1@x", "m2@x", "m3@x" });
    }

    [Fact]
    public void Aggregate_body_budget_truncates_later_bodies_and_flags_them()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var start = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        // Three 100-char bodies against a 250-char budget: first two fit whole,
        // the third gets the 50 chars left.
        for (var i = 0; i < 3; i++)
        {
            repo.Upsert(Helpers.Sample($"b{i}@x", threadId: "budget", body: new string((char)('a' + i), 100),
                    dateSent: start.AddMinutes(i)),
                "INBOX", "INBOX/cur", $"b{i}", DateTimeOffset.UtcNow);
        }

        var resp = Build(db, mcp: new McpOptions { ThreadMaxBodyChars = 250 })
            .GetThread(messageId: "b0@x", includeBodies: true);

        resp.Count.ShouldBe(3);            // no message was dropped...
        resp.Truncated.ShouldBeTrue();     // ...but the response was still clipped

        resp.Messages[0].BodyText!.Length.ShouldBe(100);
        resp.Messages[0].BodyTruncated.ShouldBeFalse();
        resp.Messages[1].BodyText!.Length.ShouldBe(100);
        resp.Messages[1].BodyTruncated.ShouldBeFalse();
        resp.Messages[2].BodyText!.Length.ShouldBe(50);
        resp.Messages[2].BodyTruncated.ShouldBeTrue();
    }

    [Fact]
    public void Bodies_after_the_budget_is_exhausted_come_back_empty()
    {
        // The test above stops at the message that spends the budget, which is
        // exactly one message short of the interesting transition: the NEXT
        // non-empty body asks SliceWindow for a zero window, and the slicer's
        // surrogate nudge evaluated text[end - 1] at end == 0 and threw
        // IndexOutOfRangeException — failing the whole get_thread call for any
        // thread long enough to spend 200,000 chars and keep going, which a
        // deep quoted reply chain does. Line coverage hid it: the else branch
        // here was already exercised, just never twice.
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var start = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        // Four 100-char bodies against 250: #3 takes the last 50, #4 gets none.
        for (var i = 0; i < 4; i++)
        {
            repo.Upsert(Helpers.Sample($"e{i}@x", threadId: "exhausted", body: new string((char)('a' + i), 100),
                    dateSent: start.AddMinutes(i)),
                "INBOX", "INBOX/cur", $"e{i}", DateTimeOffset.UtcNow);
        }

        var resp = Build(db, mcp: new McpOptions { ThreadMaxBodyChars = 250 })
            .GetThread(messageId: "e0@x", includeBodies: true);

        resp.Count.ShouldBe(4);
        resp.Truncated.ShouldBeTrue();
        resp.Messages[2].BodyText!.Length.ShouldBe(50);
        // Empty, not null: null is what "includeBodies was false" means, and a
        // client that can't tell those apart can't tell an empty message from
        // one whose body it never asked for.
        resp.Messages[3].BodyText.ShouldBe(string.Empty);
        resp.Messages[3].BodyTruncated.ShouldBeTrue();
    }

    [Fact]
    public void A_zero_body_budget_returns_empty_bodies_rather_than_failing()
    {
        // Same crash from the other direction, and without needing a long
        // thread: an operator who sets ThreadMaxBodyChars to 0 to turn body
        // inlining off hit it on the FIRST non-empty message.
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        repo.Upsert(Helpers.Sample("zero@x", threadId: "zero", body: "hello"),
            "INBOX", "INBOX/cur", "zero", DateTimeOffset.UtcNow);

        var resp = Build(db, mcp: new McpOptions { ThreadMaxBodyChars = 0 })
            .GetThread(messageId: "zero@x", includeBodies: true);

        resp.Messages[0].BodyText.ShouldBe(string.Empty);
        resp.Messages[0].BodyTruncated.ShouldBeTrue();
        resp.Truncated.ShouldBeTrue();
    }

    [Fact]
    public void An_empty_body_under_an_exhausted_budget_is_not_flagged_truncated()
    {
        // The zero-window path must not swallow the distinction between "there
        // was nothing to return" and "there was something and no room for it".
        // An empty body costs nothing, so it fits any budget including a spent
        // one, and a truncation flag on it would be a lie that teaches the
        // model to distrust the flag where it matters.
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var start = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        repo.Upsert(Helpers.Sample("f0@x", threadId: "empties", body: new string('a', 100), dateSent: start),
            "INBOX", "INBOX/cur", "f0", DateTimeOffset.UtcNow);
        repo.Upsert(Helpers.Sample("f1@x", threadId: "empties", body: "", dateSent: start.AddMinutes(1)),
            "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);

        var resp = Build(db, mcp: new McpOptions { ThreadMaxBodyChars = 100 })
            .GetThread(messageId: "f0@x", includeBodies: true);

        resp.Messages[1].BodyText.ShouldBe(string.Empty);
        resp.Messages[1].BodyTruncated.ShouldBeFalse();
        resp.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void Body_budget_is_not_spent_when_bodies_were_not_requested()
    {
        // The budget only governs BodyText. Without includeBodies there are no
        // bodies to spend it on, so a huge thread of snippets must not report
        // itself truncated — a false `truncated` teaches the model to distrust
        // the flag on the responses where it matters.
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var start = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 3; i++)
        {
            repo.Upsert(Helpers.Sample($"s{i}@x", threadId: "snip", body: new string('z', 500),
                    dateSent: start.AddMinutes(i)),
                "INBOX", "INBOX/cur", $"s{i}", DateTimeOffset.UtcNow);
        }

        var resp = Build(db, mcp: new McpOptions { ThreadMaxBodyChars = 10 }).GetThread(messageId: "s0@x");

        resp.Truncated.ShouldBeFalse();
        resp.Messages.ShouldAllBe(m => m.BodyText == null && !m.BodyTruncated);
    }

    [Fact]
    public void Untruncated_threads_report_totalCount_equal_to_count()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        repo.Upsert(Helpers.Sample("only@x", threadId: "t", body: "short"),
            "INBOX", "INBOX/cur", "o", DateTimeOffset.UtcNow);

        var resp = Build(db).GetThread(messageId: "only@x", includeBodies: true);

        resp.Count.ShouldBe(1);
        resp.TotalCount.ShouldBe(1);
        resp.Truncated.ShouldBeFalse();
        resp.Messages[0].BodyTruncated.ShouldBeFalse();
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
