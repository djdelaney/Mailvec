using Mailvec.Core.Embedding;
using Microsoft.Data.Sqlite;

namespace Mailvec.Core.Data;

public sealed class ChunkRepository(ConnectionFactory connections)
{
    /// <summary>
    /// Replaces all chunks (and their vectors) for a message and stamps embedded_at.
    /// Chunks and vectors are written in a single transaction so the message is
    /// never visible to search with a partial vector set.
    ///
    /// When <paramref name="checkContentHash"/> is set, the write is guarded:
    /// the message's current content_hash (and, when
    /// <paramref name="expectedEmbedEpoch"/> is supplied, its embed_epoch) is
    /// re-read inside this transaction (a BEGIN IMMEDIATE, so it's consistent
    /// with the writes that follow) and the whole write is abandoned — nothing
    /// committed, returns false — if the row is gone or either value moved.
    /// The embedder snapshots both before its minutes-long Ollama call and
    /// passes them here so a re-queue committed in the meantime is not
    /// clobbered. content_hash catches body changes; embed_epoch catches
    /// hash-preserving re-queues (attachment re-extraction, OCR write-back,
    /// inline-image backfill — writers that clear embedded_at without touching
    /// the body). Without the guard the embedder would write vectors built
    /// from the OLD content and stamp a fresh embedded_at, leaving stale
    /// vectors against new FTS text with nothing to trigger a re-embed —
    /// permanent silent divergence. Skipped messages keep embedded_at = NULL
    /// and are re-embedded (against the new content) on the next poll.
    /// Returns true when the chunks were written, false when the guard
    /// skipped the write.
    /// </summary>
    /// <para>
    /// <paramref name="expectedEmbeddingModel"/> guards against a mid-run
    /// `mailvec switch-model`: the write re-reads `metadata.embedding_model`
    /// inside the same transaction and skips when it no longer matches the
    /// model this embedder was configured (and startup-verified) against.
    /// Without it, a still-running embedder would re-embed the entire
    /// re-queued archive with the OLD model after a same-dimension switch —
    /// the inserts succeed, the startup check never re-runs, and the vector
    /// space is silently mixed. The startup check alone can't catch this;
    /// only a check inside the write transaction can.
    /// </para>
    public bool ReplaceChunksForMessage(
        long messageId,
        IReadOnlyList<TextChunk> chunks,
        IReadOnlyList<float[]> vectors,
        DateTimeOffset embeddedAt,
        string? expectedContentHash = null,
        bool checkContentHash = false,
        long? expectedEmbedEpoch = null,
        string? expectedEmbeddingModel = null)
    {
        if (chunks.Count != vectors.Count)
            throw new ArgumentException($"Chunk/vector count mismatch: {chunks.Count} vs {vectors.Count}");

        using var conn = connections.Open();
        using var tx = conn.BeginTransaction();

        if (checkContentHash && !SnapshotStillCurrent(conn, tx, messageId, expectedContentHash, expectedEmbedEpoch))
            return false;

        if (expectedEmbeddingModel is not null && !EmbeddingModelMatches(conn, tx, expectedEmbeddingModel))
            return false;

        DeleteChunks(conn, tx, messageId);

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunkId = InsertChunk(conn, tx, messageId, chunks[i]);
            InsertEmbedding(conn, tx, chunkId, vectors[i]);
        }

        UpdateEmbeddedAt(conn, tx, messageId, embeddedAt);
        tx.Commit();
        return true;
    }

    /// <summary>
    /// True if the message still exists, its content_hash equals the snapshot
    /// the caller captured (both may be NULL for legacy pre-v3 rows, which
    /// compare equal), and — when an expected embed_epoch is supplied — its
    /// re-queue counter hasn't moved either. A missing row (deleted since the
    /// snapshot) returns false so we don't resurrect vectors for a purged
    /// message.
    /// </summary>
    private static bool SnapshotStillCurrent(
        SqliteConnection conn, SqliteTransaction tx, long messageId, string? expectedHash, long? expectedEmbedEpoch)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT content_hash, embed_epoch FROM messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", messageId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return false;
        var currentHash = reader.IsDBNull(0) ? null : reader.GetString(0);
        if (!string.Equals(currentHash, expectedHash, StringComparison.Ordinal)) return false;
        return expectedEmbedEpoch is null || reader.GetInt64(1) == expectedEmbedEpoch.Value;
    }

    private static bool EmbeddingModelMatches(SqliteConnection conn, SqliteTransaction tx, string expectedModel)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM metadata WHERE key = 'embedding_model'";
        var current = cmd.ExecuteScalar() as string;
        return string.Equals(current, expectedModel, StringComparison.Ordinal);
    }

    public int CountForMessage(long messageId)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks WHERE message_id = $mid";
        cmd.Parameters.AddWithValue("$mid", messageId);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Count chunk_embeddings rows whose chunk_id no longer exists in chunks.
    /// These accumulate from historical code paths that assumed FK CASCADE
    /// fired across the vec0 virtual table (it doesn't). When a new chunk is
    /// inserted and SQLite assigns it a rowid (MAX(id)+1, since chunks uses
    /// INTEGER PRIMARY KEY without AUTOINCREMENT) that collides with one of
    /// these orphans, the embedder fails with `UNIQUE constraint failed on
    /// chunk_embeddings primary key` and the message gets stuck unembedded.
    /// </summary>
    public int CountOrphanEmbeddings()
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunk_embeddings WHERE chunk_id NOT IN (SELECT id FROM chunks)";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Delete chunk_embeddings rows whose chunk_id no longer exists in chunks.
    /// See <see cref="CountOrphanEmbeddings"/> for why these exist. Idempotent.
    /// </summary>
    public int DeleteOrphanEmbeddings()
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chunk_embeddings WHERE chunk_id NOT IN (SELECT id FROM chunks)";
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Resets embedded_at and removes existing chunks/vectors so the embedder will re-process.</summary>
    public int ClearEmbeddings(string? folderFilter = null)
    {
        using var conn = connections.Open();
        using var tx = conn.BeginTransaction();

        // chunk_embeddings is a vec0 virtual table; FOREIGN KEY ... CASCADE
        // does not fire across vec0, so we must delete from it explicitly
        // BEFORE deleting from chunks (otherwise the chunk_id values we need
        // to target are gone). Also covers any orphan rows from earlier
        // versions of this method that assumed CASCADE worked.
        using (var deleteEmbeddings = conn.CreateCommand())
        {
            deleteEmbeddings.Transaction = tx;
            deleteEmbeddings.CommandText = folderFilter is null
                ? "DELETE FROM chunk_embeddings"
                : """
                  DELETE FROM chunk_embeddings
                  WHERE chunk_id IN (
                      SELECT c.id FROM chunks c
                      JOIN messages m ON m.id = c.message_id
                      WHERE m.folder = $f
                  )
                  """;
            if (folderFilter is not null) deleteEmbeddings.Parameters.AddWithValue("$f", folderFilter);
            deleteEmbeddings.ExecuteNonQuery();
        }

        using (var deleteChunks = conn.CreateCommand())
        {
            deleteChunks.Transaction = tx;
            deleteChunks.CommandText = folderFilter is null
                ? "DELETE FROM chunks"
                : "DELETE FROM chunks WHERE message_id IN (SELECT id FROM messages WHERE folder = $f)";
            if (folderFilter is not null) deleteChunks.Parameters.AddWithValue("$f", folderFilter);
            deleteChunks.ExecuteNonQuery();
        }

        int affected;
        using (var clearStamp = conn.CreateCommand())
        {
            clearStamp.Transaction = tx;
            clearStamp.CommandText = folderFilter is null
                ? "UPDATE messages SET embedded_at = NULL, embed_epoch = embed_epoch + 1"
                : "UPDATE messages SET embedded_at = NULL, embed_epoch = embed_epoch + 1 WHERE folder = $f";
            if (folderFilter is not null) clearStamp.Parameters.AddWithValue("$f", folderFilter);
            affected = clearStamp.ExecuteNonQuery();
        }

        tx.Commit();
        return affected;
    }

    /// <summary>
    /// Clear embeddings (and chunks) for a single message and reset its
    /// embedded_at stamp so the embedder picks it up on the next poll.
    /// Used by the indexer when a content-hash change indicates an upstream
    /// body mutation has invalidated the existing vectors.
    /// </summary>
    public void ClearEmbeddingsForMessage(long messageId)
    {
        using var conn = connections.Open();
        using var tx = conn.BeginTransaction();
        DeleteChunks(conn, tx, messageId);
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE messages SET embedded_at = NULL, embed_epoch = embed_epoch + 1 WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", messageId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static void DeleteChunks(SqliteConnection conn, SqliteTransaction tx, long messageId)
    {
        // chunk_embeddings has chunk_id PRIMARY KEY but no FK to chunks (vec0 doesn't support that),
        // so we delete from both tables explicitly.
        using var fetchIds = conn.CreateCommand();
        fetchIds.Transaction = tx;
        fetchIds.CommandText = "SELECT id FROM chunks WHERE message_id = $mid";
        fetchIds.Parameters.AddWithValue("$mid", messageId);

        var ids = new List<long>();
        using (var reader = fetchIds.ExecuteReader())
        {
            while (reader.Read()) ids.Add(reader.GetInt64(0));
        }

        if (ids.Count > 0)
        {
            using var delEmbed = conn.CreateCommand();
            delEmbed.Transaction = tx;
            delEmbed.CommandText = "DELETE FROM chunk_embeddings WHERE chunk_id = $id";
            var p = delEmbed.Parameters.Add("$id", SqliteType.Integer);
            foreach (var id in ids) { p.Value = id; delEmbed.ExecuteNonQuery(); }
        }

        using var delChunks = conn.CreateCommand();
        delChunks.Transaction = tx;
        delChunks.CommandText = "DELETE FROM chunks WHERE message_id = $mid";
        delChunks.Parameters.AddWithValue("$mid", messageId);
        delChunks.ExecuteNonQuery();
    }

    private static long InsertChunk(SqliteConnection conn, SqliteTransaction tx, long messageId, TextChunk chunk)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO chunks(message_id, chunk_index, chunk_text, token_count, source, attachment_id)
            VALUES ($mid, $idx, $text, $tok, $src, $aid)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$mid", messageId);
        cmd.Parameters.AddWithValue("$idx", chunk.Index);
        cmd.Parameters.AddWithValue("$text", chunk.Text);
        cmd.Parameters.AddWithValue("$tok", chunk.EstimatedTokenCount);
        cmd.Parameters.AddWithValue("$src", chunk.Source);
        cmd.Parameters.AddWithValue("$aid", (object?)chunk.AttachmentId ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void InsertEmbedding(SqliteConnection conn, SqliteTransaction tx, long chunkId, float[] vector)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO chunk_embeddings(chunk_id, embedding) VALUES ($id, $vec)";
        cmd.Parameters.AddWithValue("$id", chunkId);
        cmd.Parameters.Add("$vec", SqliteType.Blob).Value = VectorBlob.Serialize(vector);
        cmd.ExecuteNonQuery();
    }

    private static void UpdateEmbeddedAt(SqliteConnection conn, SqliteTransaction tx, long messageId, DateTimeOffset at)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE messages SET embedded_at = $at WHERE id = $id";
        cmd.Parameters.AddWithValue("$at", at.ToString("O"));
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// sqlite-vec stores FLOAT[N] columns as packed little-endian float32 bytes.
/// </summary>
public static class VectorBlob
{
    public static byte[] Serialize(float[] vec)
    {
        var bytes = new byte[vec.Length * sizeof(float)];
        Buffer.BlockCopy(vec, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] Deserialize(byte[] bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
            throw new ArgumentException($"Vector blob length {bytes.Length} not a multiple of {sizeof(float)}");
        var vec = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vec, 0, bytes.Length);
        return vec;
    }
}
