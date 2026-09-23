using Mailvec.Cli.Commands;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Parsing.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Tests;

public class RebuildBodiesCommandTests
{
    [Fact]
    public void Reports_nothing_to_do_when_no_messages_have_html_body()
    {
        using var ctx = new TestServiceProvider();
        var writer = new StringWriter();
        var err = new StringWriter();

        var exit = RebuildBodiesCommand.Execute(ctx.Services, reembed: false, writer, err, apply: true);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("No messages with body_html");
    }

    [Fact]
    public void A_parser_outage_stops_the_run_instead_of_logging_an_error_per_message()
    {
        // Phase 3 of the parser isolation. BodyTextFromHtml crosses to the
        // parse service in the container; when it is down every remaining row
        // would fail identically, and "N errors" would misreport an outage as
        // N bad messages. Rows converted before the outage stay committed.
        using var ctx = new TestServiceProvider();
        ctx.AddOption<ParserOptions>(o => o.UnavailableWaitSeconds = 0); // stay down: no wait
        ctx.UseParser(new FaultingParser
        {
            Fault = op => op == nameof(IMailParser.BodyTextFromHtml) ? FaultingParser.Unavailable() : null,
        }).Rebuild();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        foreach (var id in new[] { "a@x", "b@x", "c@x" })
        {
            messages.Upsert(
                new ParsedMessage(
                    MessageId: id, ThreadId: id, Subject: "Hi",
                    FromAddress: "alice@example.com", FromName: null,
                    ToAddresses: [], CcAddresses: [],
                    DateSent: DateTimeOffset.UtcNow,
                    BodyText: "stale plaintext",
                    BodyHtml: "<html><body><p>Fresh</p></body></html>",
                    RawHeaders: $"Message-ID: <{id}>\r\n",
                    SizeBytes: 100, ContentHash: "h-" + id, Attachments: []),
                "INBOX", "INBOX/cur", id, DateTimeOffset.UtcNow);
        }
        var writer = new StringWriter();
        var err = new StringWriter();

        var exit = RebuildBodiesCommand.Execute(ctx.Services, reembed: false, writer, err, apply: true);

        exit.ShouldBe(1);
        writer.ToString().ShouldContain("STOPPED");
        writer.ToString().ShouldContain("(0 errors)", Case.Sensitive, "an outage is not a conversion error");
        err.ToString().Split("unavailable").Length.ShouldBe(2, "reported once, not per row");
        messages.GetByMessageId("a@x")!.BodyText.ShouldBe("stale plaintext");
    }

    [Fact]
    public void A_rebuild_spanning_a_parse_host_recycle_converts_every_row()
    {
        // Review finding 5, the case that made this command unfinishable: it
        // re-selects every row each run, so a stop at the host's request
        // budget restarted from row one forever. The recycle lands mid-batch
        // (second row); with the wait, the run converts all three.
        var calls = 0;
        using var ctx = new TestServiceProvider();
        ctx.AddOption<ParserOptions>(o => o.UnavailableWaitSeconds = 30);
        ctx.UseParser(new FaultingParser
        {
            Fault = op => op == nameof(IMailParser.BodyTextFromHtml) && ++calls == 2 ? FaultingParser.Unavailable() : null,
        }).Rebuild();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        foreach (var id in new[] { "a@x", "b@x", "c@x" })
        {
            messages.Upsert(
                new ParsedMessage(
                    MessageId: id, ThreadId: id, Subject: "Hi",
                    FromAddress: "alice@example.com", FromName: null,
                    ToAddresses: [], CcAddresses: [],
                    DateSent: DateTimeOffset.UtcNow,
                    BodyText: "stale plaintext",
                    BodyHtml: "<html><body><p>Fresh</p></body></html>",
                    RawHeaders: $"Message-ID: <{id}>\r\n",
                    SizeBytes: 100, ContentHash: "h-" + id, Attachments: []),
                "INBOX", "INBOX/cur", id, DateTimeOffset.UtcNow);
        }
        var writer = new StringWriter();
        var err = new StringWriter();

        var exit = RebuildBodiesCommand.Execute(ctx.Services, reembed: false, writer, err, apply: true);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("Updated body_text on 3 messages (0 errors)");
        writer.ToString().ShouldNotContain("STOPPED");
        err.ToString().ShouldContain("parse service is back");
        foreach (var id in new[] { "a@x", "b@x", "c@x" })
            messages.GetByMessageId(id)!.BodyText.ShouldNotBeNull().ShouldContain("Fresh");
    }

    [Fact]
    public void A_row_the_parser_fails_on_keeps_its_body_text()
    {
        // Review follow-up F2's caller-level half: with required-member
        // enforcement on the wire, an incomplete response is a Crashed
        // exception rather than a null result, and this command's per-row
        // catch counts it as an error and writes nothing for that row.
        using var ctx = new TestServiceProvider();
        ctx.AddOption<ParserOptions>(o => o.UnavailableWaitSeconds = 0);
        ctx.UseParser(new FaultingParser
        {
            Fault = op => op == nameof(IMailParser.BodyTextFromHtml) ? FaultingParser.Crashed() : null,
        }).Rebuild();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        long id = messages.Upsert(
            new ParsedMessage(
                MessageId: "keep@x", ThreadId: "keep@x", Subject: "Hi",
                FromAddress: "alice@example.com", FromName: null,
                ToAddresses: [], CcAddresses: [],
                DateSent: DateTimeOffset.UtcNow,
                BodyText: "the real body", BodyHtml: "<p>Fresh</p>",
                RawHeaders: "Message-ID: <keep@x>\r\n",
                SizeBytes: 100, ContentHash: "h", Attachments: []),
            "INBOX", "INBOX/cur", "keep", DateTimeOffset.UtcNow);
        var writer = new StringWriter();

        var exit = RebuildBodiesCommand.Execute(ctx.Services, reembed: false, writer, new StringWriter(), apply: true);

        exit.ShouldBe(1, "one error");
        writer.ToString().ShouldContain("(1 errors)");
        messages.GetById(id)!.BodyText.ShouldBe("the real body");
    }

    [Fact]
    public void Rebuilds_body_text_from_stored_body_html()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();

        long id = messages.Upsert(
            new ParsedMessage(
                MessageId: "a@x",
                ThreadId: "a@x",
                Subject: "Hi",
                FromAddress: "alice@example.com",
                FromName: null,
                ToAddresses: [],
                CcAddresses: [],
                DateSent: DateTimeOffset.UtcNow,
                BodyText: "stale plaintext",
                BodyHtml: "<html><body><p>Fresh <b>HTML</b> content</p></body></html>",
                RawHeaders: "Message-ID: <a@x>\r\n",
                SizeBytes: 100,
                ContentHash: "h",
                Attachments: []),
            "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var err = new StringWriter();
        var exit = RebuildBodiesCommand.Execute(ctx.Services, reembed: false, writer, err, apply: true);

        exit.ShouldBe(0);
        var msg = messages.GetById(id).ShouldNotBeNull();
        msg.BodyText.ShouldNotBe("stale plaintext");
        msg.BodyText.ShouldNotBeNull().ShouldContain("Fresh");
        writer.ToString().ShouldContain("Updated body_text on 1");
    }

    [Fact]
    public void Reembed_flag_clears_embedded_at_so_embedder_will_redo_vectors()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        var chunks = ctx.Services.GetRequiredService<ChunkRepository>();

        long id = messages.Upsert(
            new ParsedMessage(
                MessageId: "a@x",
                ThreadId: "a@x",
                Subject: "Hi",
                FromAddress: "alice@example.com",
                FromName: null,
                ToAddresses: [],
                CcAddresses: [],
                DateSent: DateTimeOffset.UtcNow,
                BodyText: "stale",
                BodyHtml: "<p>fresh</p>",
                RawHeaders: "Message-ID: <a@x>\r\n",
                SizeBytes: 100,
                ContentHash: "h",
                Attachments: []),
            "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        // Pretend the message was already embedded against the stale body.
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "stale", 1)], [HotVector(0)], DateTimeOffset.UtcNow);
        EmbeddedAtIsSet(ctx, id).ShouldBeTrue();

        var writer = new StringWriter();
        var err = new StringWriter();
        var exit = RebuildBodiesCommand.Execute(ctx.Services, reembed: true, writer, err, apply: true);

        exit.ShouldBe(0);
        // Vectors cleared, embedded_at NULL — embedder will pick this up.
        chunks.CountForMessage(id).ShouldBe(0);
        EmbeddedAtIsSet(ctx, id).ShouldBeFalse();
        writer.ToString().ShouldContain("Re-queued 1 changed messages");
    }

    [Fact]
    public void Reembed_requeues_only_the_rows_whose_body_changed()
    {
        // --reembed used to finish with a corpus-wide ClearEmbeddings: every
        // row re-queued whether or not its text moved — on a CPU-only
        // embedding host, weeks of work for a converter change that touched a
        // handful of messages. Now the re-queue rides in the UPDATE that
        // rewrote the body, so a row whose converted text already matches is
        // never touched.
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        var chunks = ctx.Services.GetRequiredService<ChunkRepository>();

        long changed = messages.Upsert(
            Sample("html@x", bodyText: "stale", bodyHtml: "<p>fresh</p>"),
            "INBOX", "INBOX/cur", "html", DateTimeOffset.UtcNow);
        // Converts to exactly its stored text: must keep its vectors.
        long unchanged = messages.Upsert(
            Sample("same@x", bodyText: "fresh", bodyHtml: "<p>fresh</p>"),
            "INBOX", "INBOX/cur", "same", DateTimeOffset.UtcNow);
        long plain = messages.Upsert(
            Sample("plain@x", bodyText: "plain body", bodyHtml: null),
            "INBOX", "INBOX/cur", "plain", DateTimeOffset.UtcNow);
        foreach (var id in new[] { changed, unchanged, plain })
            chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "x", 1)], [HotVector(0)], DateTimeOffset.UtcNow);
        var before = new[] { changed, unchanged, plain }.ToDictionary(id => id, id => EpochOf(ctx, id));

        var writer = new StringWriter();
        RebuildBodiesCommand.Execute(ctx.Services, reembed: true, writer, new StringWriter(), apply: true).ShouldBe(0);

        BodyTextOf(ctx, changed).ShouldBe("fresh");
        (EpochOf(ctx, changed) - before[changed]).ShouldBe(1, "re-queued by the transaction that rewrote it");
        EmbeddedAtIsSet(ctx, changed).ShouldBeFalse();
        chunks.CountForMessage(changed).ShouldBe(0, "stale vectors dropped with the rewrite");

        EpochOf(ctx, unchanged).ShouldBe(before[unchanged], "text already matched: vectors kept");
        EmbeddedAtIsSet(ctx, unchanged).ShouldBeTrue();
        EpochOf(ctx, plain).ShouldBe(before[plain], "no body_html: never a candidate");
        writer.ToString().ShouldContain("Re-queued 1 changed messages");
    }

    [Fact]
    public void Without_apply_nothing_is_written_and_the_change_count_is_reported()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        long a = messages.Upsert(Sample("a@x", bodyText: "stale", bodyHtml: "<p>fresh</p>"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);
        long b = messages.Upsert(Sample("b@x", bodyText: "fresh", bodyHtml: "<p>fresh</p>"), "INBOX", "INBOX/cur", "b", DateTimeOffset.UtcNow);
        var epochs = (A: EpochOf(ctx, a), B: EpochOf(ctx, b));

        var writer = new StringWriter();
        RebuildBodiesCommand.Execute(ctx.Services, reembed: true, writer, new StringWriter(), apply: false).ShouldBe(0);

        BodyTextOf(ctx, a).ShouldBe("stale");
        EpochOf(ctx, a).ShouldBe(epochs.A);
        EpochOf(ctx, b).ShouldBe(epochs.B);
        writer.ToString().ShouldContain("DRY RUN: 1 of 2 bodies would change");
        writer.ToString().ShouldContain("--apply");
    }

    [Fact]
    public void A_body_the_indexer_changed_mid_run_is_not_overwritten_with_text_from_the_old_html()
    {
        // The conversion runs outside any transaction (it crosses to the parse
        // service). An indexer upsert landing in that window used to be
        // overwritten with text converted from the OLD HTML — FTS then
        // disagreed with the stored HTML and vectors, permanently. Simulated
        // by rewriting the row's HTML from inside the conversion call.
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        long id = messages.Upsert(Sample("race@x", bodyText: "stale", bodyHtml: "<p>old html</p>"), "INBOX", "INBOX/cur", "race", DateTimeOffset.UtcNow);
        ctx.UseParser(new FaultingParser
        {
            Fault = op =>
            {
                if (op == nameof(IMailParser.BodyTextFromHtml))
                {
                    using var conn = ctx.Connections.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE messages SET body_html = '<p>new html</p>', body_text = 'new html' WHERE id = $id";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                return null;
            },
        }).Rebuild();

        var writer = new StringWriter();
        RebuildBodiesCommand.Execute(ctx.Services, reembed: true, writer, new StringWriter(), apply: true).ShouldBe(0);

        BodyTextOf(ctx, id).ShouldBe("new html", "the indexer's newer body survives");
        writer.ToString().ShouldContain("SKIPPED 1");
    }

    [Fact]
    public void Without_reembed_the_body_rewrite_leaves_embedding_state_alone()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        long id = messages.Upsert(
            Sample("noreembed@x", bodyText: "stale", bodyHtml: "<p>fresh</p>"),
            "INBOX", "INBOX/cur", "nr", DateTimeOffset.UtcNow);
        var epochBefore = EpochOf(ctx, id);

        RebuildBodiesCommand.Execute(ctx.Services, reembed: false, new StringWriter(), new StringWriter(), apply: true)
            .ShouldBe(0);

        BodyTextOf(ctx, id).ShouldBe("fresh", "the body is rebuilt regardless of --reembed");
        EpochOf(ctx, id).ShouldBe(epochBefore, "--reembed opts out of the re-queue entirely");
    }

    private static ParsedMessage Sample(string id, string bodyText, string? bodyHtml) => new(
        MessageId: id, ThreadId: id, Subject: "Hi",
        FromAddress: "alice@example.com", FromName: null,
        ToAddresses: [], CcAddresses: [], DateSent: DateTimeOffset.UtcNow,
        BodyText: bodyText, BodyHtml: bodyHtml,
        RawHeaders: $"Message-ID: <{id}>\r\n", SizeBytes: 100, ContentHash: "h-" + id,
        Attachments: []);

    private static long EpochOf(TestServiceProvider ctx, long id) =>
        Convert.ToInt64(Scalar(ctx, "SELECT embed_epoch FROM messages WHERE id = $id", id),
            System.Globalization.CultureInfo.InvariantCulture);

    private static string? BodyTextOf(TestServiceProvider ctx, long id) =>
        Scalar(ctx, "SELECT body_text FROM messages WHERE id = $id", id) as string;

    private static object? Scalar(TestServiceProvider ctx, string sql, long id)
    {
        using var conn = ctx.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private static bool EmbeddedAtIsSet(TestServiceProvider ctx, long id)
    {
        using var conn = ctx.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT embedded_at FROM messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is string;
    }

    private static float[] HotVector(int hot, int dim = 1024)
    {
        var v = new float[dim];
        v[hot] = 1f;
        return v;
    }
}
