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

    [Fact]
    public void PurgeSoftDeleted_removes_chunks_and_chunk_embeddings_for_purged_messages()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long keep = messages.Upsert(Sample("keep@x"), "INBOX", "INBOX/cur", "fk", now);
        long doomed = messages.Upsert(Sample("doomed@x"), "INBOX", "INBOX/cur", "fd", now);

        chunks.ReplaceChunksForMessage(keep, [new TextChunk(0, "k", 1)], [Hot(0)], now);
        chunks.ReplaceChunksForMessage(doomed, [new TextChunk(0, "d1", 1), new TextChunk(1, "d2", 1)], [Hot(1), Hot(2)], now);

        // Pre-purge: both messages have vectors and the global table has 3 rows.
        VectorCount(db, keep).ShouldBe(1);
        VectorCount(db, doomed).ShouldBe(2);
        TotalVectorCount(db).ShouldBe(3);

        messages.MarkDeleted([doomed], now);
        messages.PurgeSoftDeleted().ShouldBe(1);

        // The kept message's chunks/vectors are untouched.
        chunks.CountForMessage(keep).ShouldBe(1);
        VectorCount(db, keep).ShouldBe(1);

        // The doomed message's chunks cascaded; its vec0 rows were removed
        // explicitly (FK cascade doesn't fire across vec0 virtual tables).
        TotalVectorCount(db).ShouldBe(1);
    }

    [Fact]
    public void CountOrphanEmbeddings_returns_zero_when_every_embedding_has_a_chunk()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "fa", now);
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "a", 1), new TextChunk(1, "b", 1)], [Hot(0), Hot(1)], now);

        chunks.CountOrphanEmbeddings().ShouldBe(0);
    }

    [Fact]
    public void DeleteOrphanEmbeddings_removes_only_unattached_rows()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long alive = messages.Upsert(Sample("alive@x"), "INBOX", "INBOX/cur", "fa", now);
        long doomed = messages.Upsert(Sample("doomed@x"), "INBOX", "INBOX/cur", "fd", now);

        chunks.ReplaceChunksForMessage(alive, [new TextChunk(0, "a", 1)], [Hot(0)], now);
        chunks.ReplaceChunksForMessage(doomed, [new TextChunk(0, "d1", 1), new TextChunk(1, "d2", 1)], [Hot(1), Hot(2)], now);

        // Reproduce the historical leak: delete the chunks rows directly,
        // leaving the chunk_embeddings rows behind. (Production code paths
        // delete embeddings first, but earlier versions of this codebase
        // assumed FK CASCADE fired across vec0 and left orphans behind.)
        using (var conn = db.Connections.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM chunks WHERE message_id = $mid";
            cmd.Parameters.AddWithValue("$mid", doomed);
            cmd.ExecuteNonQuery();
        }

        chunks.CountOrphanEmbeddings().ShouldBe(2);
        TotalVectorCount(db).ShouldBe(3);

        chunks.DeleteOrphanEmbeddings().ShouldBe(2);

        chunks.CountOrphanEmbeddings().ShouldBe(0);
        TotalVectorCount(db).ShouldBe(1);
        VectorCount(db, alive).ShouldBe(1);
    }

    [Fact]
    public void DeleteOrphanEmbeddings_is_idempotent_when_there_are_no_orphans()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "fa", now);
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "a", 1)], [Hot(0)], now);

        chunks.DeleteOrphanEmbeddings().ShouldBe(0);

        // Real data untouched.
        chunks.CountForMessage(id).ShouldBe(1);
        VectorCount(db, id).ShouldBe(1);
    }

    [Fact]
    public void ReplaceChunksForMessage_throws_when_chunk_and_vector_counts_disagree()
    {
        // The guard prevents silent vector-space corruption: if the embedder
        // ever produced a different number of vectors than chunks, we'd write
        // mismatched (chunk_id, embedding) pairs.
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);

        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "fa", DateTimeOffset.UtcNow);

        Should.Throw<ArgumentException>(() => chunks.ReplaceChunksForMessage(
            id,
            [new TextChunk(0, "alpha", 1), new TextChunk(1, "beta", 1)],
            [Hot(0)],
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ReplaceChunksForMessage_with_no_chunks_clears_existing_state_and_stamps_embedded_at()
    {
        // Empty-body messages take this path so the embedder can mark them
        // embedded with zero chunks (otherwise EnumerateUnembedded loops on
        // them forever).
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "fa", now);
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "old", 1)], [Hot(0)], now);
        chunks.CountForMessage(id).ShouldBe(1);

        chunks.ReplaceChunksForMessage(id, [], [], now);

        chunks.CountForMessage(id).ShouldBe(0);
        VectorCount(db, id).ShouldBe(0);
        EmbeddedAt(db, id).ShouldNotBeNull();
    }

    [Fact]
    public void ClearEmbeddings_without_folder_filter_clears_every_message()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long inbox = messages.Upsert(Sample("inbox@x"), "INBOX", "INBOX/cur", "fi", now);
        long archive = messages.Upsert(Sample("arch@x"), "Archive.2024", "Archive.2024/cur", "fa", now);

        chunks.ReplaceChunksForMessage(inbox, [new TextChunk(0, "i", 1)], [Hot(0)], now);
        chunks.ReplaceChunksForMessage(archive, [new TextChunk(0, "a", 1)], [Hot(1)], now);

        // affected = messages whose embedded_at was reset.
        var affected = chunks.ClearEmbeddings();
        affected.ShouldBe(2);

        TotalVectorCount(db).ShouldBe(0);
        chunks.CountForMessage(inbox).ShouldBe(0);
        chunks.CountForMessage(archive).ShouldBe(0);
        EmbeddedAt(db, inbox).ShouldBeNull();
        EmbeddedAt(db, archive).ShouldBeNull();
    }

    [Fact]
    public void ClearEmbeddings_with_folder_filter_only_clears_that_folder()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long inbox = messages.Upsert(Sample("inbox@x"), "INBOX", "INBOX/cur", "fi", now);
        long archive = messages.Upsert(Sample("arch@x"), "Archive.2024", "Archive.2024/cur", "fa", now);

        chunks.ReplaceChunksForMessage(inbox, [new TextChunk(0, "i", 1)], [Hot(0)], now);
        chunks.ReplaceChunksForMessage(archive, [new TextChunk(0, "a", 1)], [Hot(1)], now);

        var affected = chunks.ClearEmbeddings(folderFilter: "INBOX");
        affected.ShouldBe(1);

        // Only INBOX cleared.
        chunks.CountForMessage(inbox).ShouldBe(0);
        VectorCount(db, inbox).ShouldBe(0);
        EmbeddedAt(db, inbox).ShouldBeNull();

        // Archive preserved.
        chunks.CountForMessage(archive).ShouldBe(1);
        VectorCount(db, archive).ShouldBe(1);
        EmbeddedAt(db, archive).ShouldNotBeNull();
    }

    [Fact]
    public void ClearEmbeddings_with_unknown_folder_is_a_noop()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long inbox = messages.Upsert(Sample("inbox@x"), "INBOX", "INBOX/cur", "fi", now);
        chunks.ReplaceChunksForMessage(inbox, [new TextChunk(0, "i", 1)], [Hot(0)], now);

        chunks.ClearEmbeddings("does-not-exist").ShouldBe(0);

        chunks.CountForMessage(inbox).ShouldBe(1);
        VectorCount(db, inbox).ShouldBe(1);
        EmbeddedAt(db, inbox).ShouldNotBeNull();
    }

    [Fact]
    public void ClearEmbeddingsForMessage_on_message_with_no_chunks_is_a_noop()
    {
        // Exercises the `ids.Count == 0` branch in DeleteChunks — a message
        // that's never been embedded but is being targeted by a content-hash
        // change. The UPDATE still runs (embedded_at was already NULL → 1
        // row touched, no observable change).
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);

        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "fa", DateTimeOffset.UtcNow);
        chunks.CountForMessage(id).ShouldBe(0);

        Should.NotThrow(() => chunks.ClearEmbeddingsForMessage(id));

        chunks.CountForMessage(id).ShouldBe(0);
        EmbeddedAt(db, id).ShouldBeNull();
    }

    private static int TotalVectorCount(TempDatabase db)
    {
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunk_embeddings";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
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
