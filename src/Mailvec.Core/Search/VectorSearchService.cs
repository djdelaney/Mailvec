using System.Text;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Models;
using Mailvec.Core.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

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
    double Distance,
    // 'body' or 'attachment' — the source of the matching chunk. Lets MCP
    // callers (Claude) know whether the relevance came from the email body
    // or from a document attached to the email, and surface the attachment
    // filename so the user can ask follow-up questions about it directly.
    string ChunkSource = "body",
    long? MatchedAttachmentId = null,
    int? MatchedAttachmentPartIndex = null,
    string? MatchedAttachmentFileName = null);

// ollamaOptions is optional so existing direct test constructions compile;
// DI always supplies it. Only QueryInstructionPrefix is read here.
public sealed class VectorSearchService(ConnectionFactory connections, IEmbeddingClient ollama, IOptions<OllamaOptions>? ollamaOptions = null)
{
    // vec0 KNN runs BEFORE our filter join, so the k nearest chunks may all be
    // filtered out. Rather than make every caller guess a filter-aware k (which
    // historically drifted — some callers inflated, some didn't, and bare
    // semantic+folder returned ~1 result), escalate k here until we have `limit`
    // post-filter hits or hit the ceiling. A filtered semantic query is a rare,
    // off-hot-path request, so the extra KNN round-trips are acceptable.
    private const int KnnEscalationFactor = 8;
    // sqlite-vec rejects a knn `k` larger than 4096 ("k value in knn query too
    // large"). That's the hard ceiling on how deep we can over-fetch; a filter
    // whose matches all sit beyond the 4096 nearest chunks can't be served by
    // KNN at all (acceptable — far better than the old fixed k=500).
    private const int Vec0MaxK = 4096;

    /// <summary>
    /// Embeds the query, runs k-nearest-neighbour against chunk_embeddings,
    /// joins back to messages (skipping soft-deleted), and returns the best
    /// chunk per message (since one message can have multiple chunks).
    /// When filters are present, k is escalated internally (see
    /// <see cref="SearchByVector"/>) so a restrictive filter can't silently
    /// starve the result set — vec0 KNN happens before the filter join.
    /// </summary>
    public async Task<IReadOnlyList<VectorHit>> SearchAsync(string query, int limit = 20, int k = 100, SearchFilters? filters = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        // Query-side instruction prefix for asymmetric (instruction-tuned)
        // models. Documents are embedded bare by the EmbeddingWorker; only
        // the query carries the instruction — that's how these models are
        // trained. Empty prefix (mxbai et al.) embeds the query unchanged.
        var prefix = ollamaOptions?.Value.QueryInstructionPrefix;
        var embedText = string.IsNullOrEmpty(prefix) ? query : prefix + query;

        var vectors = await ollama.EmbedAsync([embedText], ct).ConfigureAwait(false);
        if (vectors.Length == 0) return [];

        return SearchByVector(vectors[0], limit, k, filters);
    }

    /// <summary>
    /// Same as SearchAsync but skips the embed step. Used by tests with hand-built
    /// vectors and by HybridSearchService when the query was already embedded.
    /// <para>
    /// `k` is the KNN fetch size for the unfiltered case. When filters are
    /// present it is only a starting floor: if the post-filter set is shorter
    /// than `limit`, the KNN is re-run with progressively larger k (×8 per round,
    /// capped at <see cref="Vec0MaxK"/>) until `limit` hits are found or every
    /// available chunk has been fetched. This is the single source of truth for
    /// the "vec0 filters after KNN" workaround — callers no longer pre-inflate k.
    /// </para>
    /// </summary>
    public IReadOnlyList<VectorHit> SearchByVector(float[] queryVector, int limit, int k, SearchFilters? filters = null)
    {
        filters ??= SearchFilters.None;

        using var conn = connections.Open();

        if (filters.IsEmpty)
            return ExecuteKnn(conn, queryVector, limit, k, filters);

        // Filtered: escalate k until we have `limit` post-filter hits or we've
        // fetched every available neighbour. Bounding by the chunk count (not a
        // "result count stopped growing" heuristic) is what makes this correct —
        // in-filter matches can sit arbitrarily far down the distance ranking, so
        // we must keep widening until either limit is met or there's nothing left
        // to widen into. KnnEscalationCap backstops a pathologically large table.
        var maxK = Math.Min(Vec0MaxK, CountChunks(conn));
        if (maxK == 0) return [];
        var curK = Math.Min(Math.Max(k, limit), maxK);
        while (true)
        {
            var hits = ExecuteKnn(conn, queryVector, limit, curK, filters);
            if (hits.Count >= limit || curK >= maxK)
                return hits;
            curK = Math.Min(curK * KnnEscalationFactor, maxK);
        }
    }

    private static int CountChunks(SqliteConnection conn)
    {
        // chunks is 1:1 with chunk_embeddings (ChunkRepository writes both), and a
        // plain-table count is far cheaper than counting the vec0 virtual table.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM chunks";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static IReadOnlyList<VectorHit> ExecuteKnn(SqliteConnection conn, float[] queryVector, int limit, int k, SearchFilters filters)
    {
        using var cmd = conn.CreateCommand();
        // Take the BEST chunk per message: window over chunks ordered by distance.
        // Vec0 requires the MATCH + k constraint and ORDER BY distance.
        // Filters apply in the joined CTE — vec0 cannot itself filter on joined
        // columns, so we over-fetch the k nearest and drop non-matching messages
        // here (SearchByVector escalates k across calls when this starves).
        var sql = new StringBuilder("""
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
                    c.source    AS chunk_source,
                    c.attachment_id,
                    a.part_index AS att_part_index,
                    a.filename   AS att_filename,
                    n.distance,
                    ROW_NUMBER() OVER (PARTITION BY m.id ORDER BY n.distance) AS rn
                FROM neighbours n
                JOIN chunks c        ON c.id = n.chunk_id
                JOIN messages m      ON m.id = c.message_id
                LEFT JOIN attachments a ON a.id = c.attachment_id
                WHERE m.deleted_at IS NULL
            """);
        SearchFilterSql.Append(sql, cmd, filters);
        sql.Append("""

            )
            SELECT message_id, message_id_hdr, folder, subject, from_address, from_name, date_sent,
                   chunk_id, chunk_index, chunk_text, distance,
                   chunk_source, attachment_id, att_part_index, att_filename
            FROM joined
            WHERE rn = 1
            ORDER BY distance
            LIMIT $limit;
            """);
        cmd.CommandText = sql.ToString();
        cmd.Parameters.Add("$vec", SqliteType.Blob).Value = VectorBlob.Serialize(queryVector);
        // sqlite-vec hard-rejects k > 4096; clamp so an over-eager caller (or a
        // large unfiltered limit) can't throw instead of just returning fewer.
        cmd.Parameters.AddWithValue("$k", Math.Min(k, Vec0MaxK));
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
                Distance: reader.GetDouble(10),
                ChunkSource: reader.IsDBNull(11) ? "body" : reader.GetString(11),
                MatchedAttachmentId: reader.IsDBNull(12) ? null : reader.GetInt64(12),
                MatchedAttachmentPartIndex: reader.IsDBNull(13) ? null : reader.GetInt32(13),
                MatchedAttachmentFileName: reader.IsDBNull(14) ? null : reader.GetString(14)));
        }
        return hits;
    }
}
