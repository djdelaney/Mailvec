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

        var ftsQuery = BuildFtsQuery(query);
        if (ftsQuery.Length == 0) return [];

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
        cmd.Parameters.AddWithValue("$q", ftsQuery);
        cmd.Parameters.AddWithValue("$limit", limit);

        return Read(cmd);
    }

    /// <summary>
    /// Convert a natural-language query into an FTS5 MATCH expression. FTS5
    /// without operators treats a multi-token query as an implicit phrase
    /// (all tokens, in order) — so "plumbing services plumber quote estimate"
    /// returns 0 hits across our corpus even though many messages match a
    /// subset. We tokenize on non-alphanumeric boundaries (matches what the
    /// porter unicode61 tokenizer does) and OR-join, with each token wrapped
    /// in double quotes so FTS5 reserved words like "and"/"or"/"not"/"near"
    /// are treated as literal terms instead of operators. BM25 still orders
    /// docs that hit more terms higher, so OR is the right semantic anyway.
    /// Callers that want advanced FTS5 syntax (boolean operators, phrase
    /// quoting, column filters) signal it by including reserved characters
    /// or uppercase boolean keywords; we pass those through unchanged.
    /// </summary>
    private static string BuildFtsQuery(string raw)
    {
        if (LooksLikeAdvancedSyntax(raw)) return raw.Trim();

        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());

        if (tokens.Count == 0) return string.Empty;

        var sb = new StringBuilder(tokens.Sum(t => t.Length + 6));
        for (int i = 0; i < tokens.Count; i++)
        {
            if (i > 0) sb.Append(" OR ");
            sb.Append('"').Append(tokens[i]).Append('"');
        }
        return sb.ToString();
    }

    private static bool LooksLikeAdvancedSyntax(string q)
    {
        // Quote / parens / column-filter / prefix / column-anchor characters
        // all imply the caller is hand-crafting an FTS5 expression.
        foreach (var ch in q)
        {
            if (ch is '"' or '(' or ')' or ':' or '*' or '^') return true;
        }
        // Uppercase boolean / proximity keywords surrounded by whitespace.
        return ContainsUppercaseKeyword(q, " AND ")
            || ContainsUppercaseKeyword(q, " OR ")
            || ContainsUppercaseKeyword(q, " NOT ")
            || ContainsUppercaseKeyword(q, " NEAR(")
            || q.StartsWith("NOT ", StringComparison.Ordinal);
    }

    private static bool ContainsUppercaseKeyword(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.Ordinal);

    private static IReadOnlyList<SearchHit> Read(Microsoft.Data.Sqlite.SqliteCommand cmd)
    {
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
