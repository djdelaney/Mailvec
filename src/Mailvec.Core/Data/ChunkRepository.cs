using Mailvec.Core.Embedding;
using Microsoft.Data.Sqlite;

namespace Mailvec.Core.Data;

public sealed class ChunkRepository(ConnectionFactory connections)
{
    /// <summary>
    /// Replaces all chunks (and their vectors) for a message and stamps embedded_at.
    /// Chunks and vectors are written in a single transaction so the message is
    /// never visible to search with a partial vector set.
    /// </summary>
    public void ReplaceChunksForMessage(long messageId, IReadOnlyList<TextChunk> chunks, IReadOnlyList<float[]> vectors, DateTimeOffset embeddedAt)
    {
        if (chunks.Count != vectors.Count)
            throw new ArgumentException($"Chunk/vector count mismatch: {chunks.Count} vs {vectors.Count}");

        using var conn = connections.Open();
        using var tx = conn.BeginTransaction();

        DeleteChunks(conn, tx, messageId);

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunkId = InsertChunk(conn, tx, messageId, chunks[i]);
            InsertEmbedding(conn, tx, chunkId, vectors[i]);
        }

        UpdateEmbeddedAt(conn, tx, messageId, embeddedAt);
        tx.Commit();
    }

    public int CountForMessage(long messageId)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks WHERE message_id = $mid";
        cmd.Parameters.AddWithValue("$mid", messageId);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
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
                ? "UPDATE messages SET embedded_at = NULL"
                : "UPDATE messages SET embedded_at = NULL WHERE folder = $f";
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
            cmd.CommandText = "UPDATE messages SET embedded_at = NULL WHERE id = $id";
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
            INSERT INTO chunks(message_id, chunk_index, chunk_text, token_count)
            VALUES ($mid, $idx, $text, $tok)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$mid", messageId);
        cmd.Parameters.AddWithValue("$idx", chunk.Index);
        cmd.Parameters.AddWithValue("$text", chunk.Text);
        cmd.Parameters.AddWithValue("$tok", chunk.EstimatedTokenCount);
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
