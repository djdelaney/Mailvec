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
