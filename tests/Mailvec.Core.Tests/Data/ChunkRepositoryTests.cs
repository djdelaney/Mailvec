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

    [Fact]
    public void ReplaceChunksForMessage_writes_when_content_hash_matches()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "fa", now); // hash "hash-a@x"

        var written = chunks.ReplaceChunksForMessage(
            id, [new TextChunk(0, "alpha", 1)], [Hot(0)], now,
            expectedContentHash: "hash-a@x", checkContentHash: true);

        written.ShouldBeTrue();
        chunks.CountForMessage(id).ShouldBe(1);
        EmbeddedAt(db, id).ShouldNotBeNull();
    }

    [Fact]
    public void ReplaceChunksForMessage_skips_and_preserves_state_when_content_hash_changed()
    {
        // H2 regression: the embedder snapshots content_hash before its slow
        // Ollama call. If the indexer commits a body change meanwhile (new
        // hash), the guarded write must abandon everything — not delete the
        // freshly re-queued state and stamp stale vectors.
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "fa", now); // hash "hash-a@x"
        // Existing committed state we must not clobber.
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "current", 1)], [Hot(0)], now);
        var stampBefore = EmbeddedAt(db, id);

        // Embedder wrote from an older snapshot whose hash no longer matches.
        var written = chunks.ReplaceChunksForMessage(
            id, [new TextChunk(0, "stale-a", 1), new TextChunk(1, "stale-b", 1)], [Hot(1), Hot(2)], now.AddMinutes(1),
            expectedContentHash: "stale-hash", checkContentHash: true);

        written.ShouldBeFalse();
        // Nothing in the transaction ran: the prior single chunk and its
        // original embedded_at stamp are intact.
        chunks.CountForMessage(id).ShouldBe(1);
        VectorCount(db, id).ShouldBe(1);
        EmbeddedAt(db, id).ShouldBe(stampBefore);
    }

    [Fact]
    public void ReplaceChunksForMessage_skips_when_message_deleted()
    {
        using var db = new TempDatabase();
        var chunks = new ChunkRepository(db.Connections);

        var written = chunks.ReplaceChunksForMessage(
            messageId: 999_999, [new TextChunk(0, "x", 1)], [Hot(0)], DateTimeOffset.UtcNow,
            expectedContentHash: null, checkContentHash: true);

        written.ShouldBeFalse();
        chunks.CountForMessage(999_999).ShouldBe(0);
    }

    [Fact]
    public void ReplaceChunksForMessage_skips_when_embed_epoch_moved()
    {
        // The attachment-axis regression: a re-queue that does NOT change
        // content_hash (extract-attachments --reembed, backfill-inline-images,
        // OCR write-back) clears embedded_at while the embedder is mid-embed.
        // The hash guard alone can't see it — the write would stamp over the
        // re-queue and the new attachment text would never be vector-embedded.
        // embed_epoch is bumped by every re-queue path, so the guard catches it.
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "fa", now); // hash "hash-a@x", epoch 0
        var snapshotEpoch = EmbedEpoch(db, id);

        // Hash-preserving re-queue lands while the embedder is in Ollama.
        chunks.ClearEmbeddingsForMessage(id);
        EmbedEpoch(db, id).ShouldBe(snapshotEpoch + 1);

        // Embedder's guarded write from the pre-re-queue snapshot: hash still
        // matches, but the epoch moved — must abandon everything.
        var written = chunks.ReplaceChunksForMessage(
            id, [new TextChunk(0, "stale", 1)], [Hot(0)], now.AddMinutes(1),
            expectedContentHash: "hash-a@x", checkContentHash: true, expectedEmbedEpoch: snapshotEpoch);

        written.ShouldBeFalse();
        chunks.CountForMessage(id).ShouldBe(0);
        EmbeddedAt(db, id).ShouldBeNull();     // still re-queued for the next poll
    }

    [Fact]
    public void ReplaceChunksForMessage_writes_when_epoch_and_hash_both_match()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "fa", now);

        var written = chunks.ReplaceChunksForMessage(
            id, [new TextChunk(0, "alpha", 1)], [Hot(0)], now,
            expectedContentHash: "hash-a@x", checkContentHash: true, expectedEmbedEpoch: EmbedEpoch(db, id));

        written.ShouldBeTrue();
        chunks.CountForMessage(id).ShouldBe(1);
        EmbeddedAt(db, id).ShouldNotBeNull();
    }

    [Fact]
    public void Bulk_clear_paths_bump_embed_epoch()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "fa", now);
        var epoch0 = EmbedEpoch(db, id);

        chunks.ClearEmbeddings();                       // reindex --all path
        EmbedEpoch(db, id).ShouldBe(epoch0 + 1);

        chunks.ClearEmbeddings(folderFilter: "INBOX");  // reindex --folder path
        EmbedEpoch(db, id).ShouldBe(epoch0 + 2);
    }

    [Fact]
    public void Upsert_bumps_embed_epoch_on_content_change_but_not_on_noop_rescan()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "fa", now);
        var epoch0 = EmbedEpoch(db, id);

        // No-op rescan: same content hash — epoch untouched.
        messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "fa", now.AddMinutes(1));
        EmbedEpoch(db, id).ShouldBe(epoch0);

        // Content change: epoch bumps in the same transaction as the re-queue.
        messages.Upsert(Sample("a@x") with { BodyText = "new body", ContentHash = "hash-changed" },
            "INBOX", "INBOX/cur", "fa", now.AddMinutes(2));
        EmbedEpoch(db, id).ShouldBe(epoch0 + 1);
    }

    [Fact]
    public void ReplaceChunksForMessage_skips_when_embedding_model_switched()
    {
        // A same-dimension `mailvec switch-model` while the embedder runs:
        // metadata.embedding_model no longer matches the model this worker
        // embeds with. The write must abandon, or old-model vectors would be
        // silently mixed into the new vector space with plausible scores.
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "fa", now);

        // The DB's metadata says 'mxbai-embed-large' (fresh-DB default); this
        // worker still thinks it's embedding with 'old-model'.
        var written = chunks.ReplaceChunksForMessage(
            id, [new TextChunk(0, "alpha", 1)], [Hot(0)], now,
            expectedEmbeddingModel: "old-model");

        written.ShouldBeFalse();
        chunks.CountForMessage(id).ShouldBe(0);
        EmbeddedAt(db, id).ShouldBeNull();

        // Matching model writes normally.
        chunks.ReplaceChunksForMessage(
            id, [new TextChunk(0, "alpha", 1)], [Hot(0)], now,
            expectedEmbeddingModel: "mxbai-embed-large").ShouldBeTrue();
        chunks.CountForMessage(id).ShouldBe(1);
    }

    private static long EmbedEpoch(TempDatabase db, long messageId)
    {
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT embed_epoch FROM messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", messageId);
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
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
