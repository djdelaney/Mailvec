using Mailvec.Cli.Commands;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Parsing;
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

        var exit = RebuildBodiesCommand.Execute(ctx.Services, reembed: false, writer, err);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("No messages with body_html");
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
        var exit = RebuildBodiesCommand.Execute(ctx.Services, reembed: false, writer, err);

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
        var exit = RebuildBodiesCommand.Execute(ctx.Services, reembed: true, writer, err);

        exit.ShouldBe(0);
        // Vectors cleared, embedded_at NULL — embedder will pick this up.
        chunks.CountForMessage(id).ShouldBe(0);
        EmbeddedAtIsSet(ctx, id).ShouldBeFalse();
        writer.ToString().ShouldContain("Cleared embeddings on 1");
    }

    [Fact]
    public void Reembed_invalidates_inside_the_batch_that_rewrote_the_body()
    {
        // The re-queue used to happen ONCE, after every body batch had
        // committed. An interrupt in between left new body_text beside vectors
        // and an embedded_at built from the old text, with nothing in the
        // database marking it — a re-run repairs it (every row is re-derived
        // from body_html) but only if someone knows to run one, and a
        // half-finished maintenance command is when nobody does.
        //
        // The end state is identical either way, because the trailing bulk
        // ClearEmbeddings updates every row unconditionally — which is exactly
        // why the existing test above cannot tell the two apart. embed_epoch
        // can: it is additive, so a row the batch rewrote is bumped twice (once
        // by its own UPDATE, once by the bulk clear) while a row that was never
        // a candidate is bumped only once. Asserting the DIFFERENCE avoids
        // pinning either mechanism's exact count.
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();

        long rebuilt = messages.Upsert(
            Sample("html@x", bodyText: "stale", bodyHtml: "<p>fresh</p>"),
            "INBOX", "INBOX/cur", "html", DateTimeOffset.UtcNow);
        // No body_html, so `WHERE body_html IS NOT NULL` never selects it: it
        // can only be touched by the trailing bulk clear.
        long untouched = messages.Upsert(
            Sample("plain@x", bodyText: "plain body", bodyHtml: null),
            "INBOX", "INBOX/cur", "plain", DateTimeOffset.UtcNow);

        var before = (Rebuilt: EpochOf(ctx, rebuilt), Untouched: EpochOf(ctx, untouched));

        RebuildBodiesCommand.Execute(ctx.Services, reembed: true, new StringWriter(), new StringWriter())
            .ShouldBe(0);

        var deltaRebuilt = EpochOf(ctx, rebuilt) - before.Rebuilt;
        var deltaUntouched = EpochOf(ctx, untouched) - before.Untouched;

        deltaUntouched.ShouldBeGreaterThan(0, "the bulk clear invalidates every row");
        deltaRebuilt.ShouldBeGreaterThan(deltaUntouched,
            "a rewritten body must be re-queued by the transaction that rewrote it, " +
            "not only by the bulk clear at the end");

        // And the rebuild itself still happened.
        BodyTextOf(ctx, rebuilt).ShouldBe("fresh");
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

        RebuildBodiesCommand.Execute(ctx.Services, reembed: false, new StringWriter(), new StringWriter())
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
