using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Models;
using Mailvec.Core.Parsing;

namespace Mailvec.Core.Tests.Data;

public class ChunkRepositoryTests
{
    [Fact]
    public void ClearEmbeddingsForMessage_drops_chunks_vectors_and_clears_embedded_at()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "fa", now);

        chunks.ReplaceChunksForMessage(
            id,
            [new TextChunk(0, "alpha", 1), new TextChunk(1, "beta", 1)],
            [Hot(0), Hot(1)],
            now);

        chunks.CountForMessage(id).ShouldBe(2);
        VectorCount(db, id).ShouldBe(2);
        EmbeddedAt(db, id).ShouldNotBeNull();

        chunks.ClearEmbeddingsForMessage(id);

        chunks.CountForMessage(id).ShouldBe(0);
        VectorCount(db, id).ShouldBe(0);
        EmbeddedAt(db, id).ShouldBeNull();
    }

    [Fact]
    public void ClearEmbeddingsForMessage_does_not_touch_other_messages()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long keep = messages.Upsert(Sample("keep@x"), "INBOX", "INBOX/cur", "fk", now);
        long target = messages.Upsert(Sample("target@x"), "INBOX", "INBOX/cur", "ft", now);

        chunks.ReplaceChunksForMessage(keep, [new TextChunk(0, "k", 1)], [Hot(0)], now);
        chunks.ReplaceChunksForMessage(target, [new TextChunk(0, "t", 1)], [Hot(1)], now);

        chunks.ClearEmbeddingsForMessage(target);

        chunks.CountForMessage(keep).ShouldBe(1);
        VectorCount(db, keep).ShouldBe(1);
        EmbeddedAt(db, keep).ShouldNotBeNull();

        chunks.CountForMessage(target).ShouldBe(0);
        VectorCount(db, target).ShouldBe(0);
        EmbeddedAt(db, target).ShouldBeNull();
    }

    private static int VectorCount(TempDatabase db, long messageId)
    {
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM chunk_embeddings
            WHERE chunk_id IN (SELECT id FROM chunks WHERE message_id = $mid)
            """;
        cmd.Parameters.AddWithValue("$mid", messageId);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? EmbeddedAt(TempDatabase db, long messageId)
    {
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT embedded_at FROM messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", messageId);
        var raw = cmd.ExecuteScalar();
        return raw is string s ? s : null;
    }

    private static float[] Hot(int index, int dim = 1024)
    {
        var v = new float[dim];
        v[index] = 1f;
        return v;
    }

    private static ParsedMessage Sample(string id) => new(
        MessageId: id,
        ThreadId: id,
        Subject: id,
        FromAddress: "alice@example.com",
        FromName: null,
        ToAddresses: [],
        CcAddresses: [],
        DateSent: DateTimeOffset.UtcNow,
        BodyText: "body",
        BodyHtml: null,
        RawHeaders: $"Message-ID: <{id}>\r\n",
        SizeBytes: 100,
        ContentHash: $"hash-{id}",
        Attachments: []);
}
