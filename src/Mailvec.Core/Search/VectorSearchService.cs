using Mailvec.Core.Data;
using Mailvec.Core.Models;
using Mailvec.Core.Ollama;
using Microsoft.Data.Sqlite;

namespace Mailvec.Core.Search;

public sealed record VectorHit(
    long MessageId,
    string MessageIdHeader,
    string Folder,
    string? Subject,
    string? FromAddress,
    string? FromName,
    DateTimeOffset? DateSent,
    long ChunkId,
    int ChunkIndex,
    string ChunkText,
    double Distance);

public sealed class VectorSearchService(ConnectionFactory connections, OllamaClient ollama)
{
    /// <summary>
    /// Embeds the query, runs k-nearest-neighbour against chunk_embeddings,
    /// joins back to messages (skipping soft-deleted), and returns the best
    /// chunk per message (since one message can have multiple chunks).
    /// </summary>
    public async Task<IReadOnlyList<VectorHit>> SearchAsync(string query, int limit = 20, int k = 100, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var vectors = await ollama.EmbedAsync([query], ct).ConfigureAwait(false);
        if (vectors.Length == 0) return [];

        return SearchByVector(vectors[0], limit, k);
    }

    /// <summary>
    /// Same as SearchAsync but skips the embed step. Used by tests with hand-built
    /// vectors and by HybridSearchService when the query was already embedded.
    /// </summary>
    public IReadOnlyList<VectorHit> SearchByVector(float[] queryVector, int limit, int k)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        // Take the BEST chunk per message: window over chunks ordered by distance.
        // Vec0 requires the MATCH + k constraint and ORDER BY distance.
        cmd.CommandText = """
            WITH neighbours AS (
                SELECT chunk_id, distance
                FROM chunk_embeddings
                WHERE embedding MATCH $vec AND k = $k
                ORDER BY distance
            ),
            joined AS (
                SELECT
                    m.id        AS message_id,
                    m.message_id AS message_id_hdr,
                    m.folder,
                    m.subject,
                    m.from_address,
                    m.from_name,
                    m.date_sent,
                    c.id        AS chunk_id,
                    c.chunk_index,
                    c.chunk_text,
                    n.distance,
                    ROW_NUMBER() OVER (PARTITION BY m.id ORDER BY n.distance) AS rn
                FROM neighbours n
                JOIN chunks c   ON c.id = n.chunk_id
                JOIN messages m ON m.id = c.message_id
                WHERE m.deleted_at IS NULL
            )
            SELECT message_id, message_id_hdr, folder, subject, from_address, from_name, date_sent, chunk_id, chunk_index, chunk_text, distance
            FROM joined
            WHERE rn = 1
            ORDER BY distance
            LIMIT $limit;
            """;
        cmd.Parameters.Add("$vec", SqliteType.Blob).Value = VectorBlob.Serialize(queryVector);
        cmd.Parameters.AddWithValue("$k", k);
        cmd.Parameters.AddWithValue("$limit", limit);

        var hits = new List<VectorHit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(new VectorHit(
                MessageId: reader.GetInt64(0),
                MessageIdHeader: reader.GetString(1),
                Folder: reader.GetString(2),
                Subject: reader.IsDBNull(3) ? null : reader.GetString(3),
                FromAddress: reader.IsDBNull(4) ? null : reader.GetString(4),
                FromName: reader.IsDBNull(5) ? null : reader.GetString(5),
                DateSent: reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
                ChunkId: reader.GetInt64(7),
                ChunkIndex: reader.GetInt32(8),
                ChunkText: reader.GetString(9),
                Distance: reader.GetDouble(10)));
        }
        return hits;
    }
}
