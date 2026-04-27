using System.Text;
using Microsoft.Data.Sqlite;

namespace Mailvec.Core.Search;

/// <summary>
/// Builds AND-ed WHERE fragments for SearchFilters and binds the corresponding
/// parameters. Both keyword and vector search call this so filter semantics
/// stay identical across legs (important: hybrid RRF assumes the same set of
/// candidates is filterable from both sides).
///
/// All clauses reference messages columns aliased as `m.*`. The caller is
/// responsible for ensuring `m` is in scope at the point this is appended.
/// </summary>
internal static class SearchFilterSql
{
    public static void Append(StringBuilder sql, SqliteCommand cmd, SearchFilters filters)
    {
        if (!string.IsNullOrEmpty(filters.Folder))
        {
            sql.Append("\n  AND m.folder = $folder");
            cmd.Parameters.AddWithValue("$folder", filters.Folder);
        }
        if (filters.DateFrom is { } from)
        {
            // datetime() handles ISO-8601 stored via DateTimeOffset.ToString("O").
            // Messages with NULL date_sent are excluded when a date filter is set.
            sql.Append("\n  AND m.date_sent IS NOT NULL AND datetime(m.date_sent) >= datetime($date_from)");
            cmd.Parameters.AddWithValue("$date_from", from.ToString("O"));
        }
        if (filters.DateTo is { } to)
        {
            sql.Append("\n  AND m.date_sent IS NOT NULL AND datetime(m.date_sent) <= datetime($date_to)");
            cmd.Parameters.AddWithValue("$date_to", to.ToString("O"));
        }
        if (!string.IsNullOrEmpty(filters.FromContains))
        {
            // Match either the address or the display name; users say "from
            // Bartlett" without knowing whether that's the name or the local-part.
            sql.Append("\n  AND (LOWER(m.from_address) LIKE $from_like OR LOWER(m.from_name) LIKE $from_like)");
            cmd.Parameters.AddWithValue("$from_like", "%" + filters.FromContains.ToLowerInvariant() + "%");
        }
    }
}
