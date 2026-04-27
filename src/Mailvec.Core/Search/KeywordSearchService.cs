using System.Text;
using Mailvec.Core.Data;
using Mailvec.Core.Models;

namespace Mailvec.Core.Search;

public sealed class KeywordSearchService(ConnectionFactory connections)
{
    /// <summary>
    /// FTS5 MATCH query against subject/from/body, ordered by BM25 (lower is better).
    /// SearchHit.Bm25Score is the raw FTS5 score; smaller = more relevant.
    /// Filters (folder, date range, sender substring) are AND-ed in SQL so the
    /// LIMIT applies after filtering — important when the filter is restrictive.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit = 20, SearchFilters? filters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        filters ??= SearchFilters.None;

        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        var sql = new StringBuilder("""
            SELECT
                m.id,
                m.message_id,
                m.folder,
                m.subject,
                m.from_address,
                m.from_name,
                m.date_sent,
                snippet(messages_fts, 3, '[', ']', ' … ', 12) AS snippet,
                bm25(messages_fts) AS score
            FROM messages_fts
            JOIN messages m ON m.id = messages_fts.rowid
            WHERE messages_fts MATCH $q
              AND m.deleted_at IS NULL
            """);
        SearchFilterSql.Append(sql, cmd, filters);
        sql.Append("\nORDER BY score\nLIMIT $limit;");
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$limit", limit);

        var hits = new List<SearchHit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(new SearchHit(
                MessageId: reader.GetInt64(0),
                MessageIdHeader: reader.GetString(1),
                Folder: reader.GetString(2),
                Subject: reader.IsDBNull(3) ? null : reader.GetString(3),
                FromAddress: reader.IsDBNull(4) ? null : reader.GetString(4),
                FromName: reader.IsDBNull(5) ? null : reader.GetString(5),
                DateSent: reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
                Snippet: reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                Bm25Score: reader.GetDouble(8)));
        }
        return hits;
    }
}
