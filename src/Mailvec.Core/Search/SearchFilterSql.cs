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
            // A message matches a folder if ANY of its live copies is in it,
            // not just the attributed primary (messages.folder). sync_state is
            // the membership source: one row per live file, folder written by
            // the scanner (v8). The `m.folder = $folder` half keeps single-copy
            // semantics identical, covers rows written by pre-v8 binaries
            // whose sync_state.folder is still NULL (self-heals on the next
            // scan), and lets tests that seed messages without sync_state keep
            // working. Probe is index-only via idx_sync_state_message_folder.
            sql.Append("""

                  AND (m.folder = $folder OR EXISTS (
                        SELECT 1 FROM sync_state ss
                        WHERE ss.message_id = m.message_id AND ss.folder = $folder))
                """);
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
        // FromExact takes precedence over FromContains — exact match is strictly
        // narrower, and Claude can hit either depending on what it knows about
        // the sender. Don't AND both clauses; that's confusing semantics.
        if (!string.IsNullOrEmpty(filters.FromExact))
        {
            sql.Append("\n  AND LOWER(m.from_address) = $from_exact");
            cmd.Parameters.AddWithValue("$from_exact", filters.FromExact.ToLowerInvariant());
        }
        else if (!string.IsNullOrEmpty(filters.FromContains))
        {
            // Match either the address or the display name; users say "from
            // Acme" without knowing whether that's the name or the local-part.
            // Escape LIKE metacharacters in the value so a literal % or _ in a
            // sender ("a_b@x") matches literally instead of acting as a wildcard.
            sql.Append("\n  AND (LOWER(m.from_address) LIKE $from_like ESCAPE '\\' OR LOWER(m.from_name) LIKE $from_like ESCAPE '\\')");
            cmd.Parameters.AddWithValue("$from_like", "%" + EscapeLike(filters.FromContains.ToLowerInvariant()) + "%");
        }
    }

    // Escape the LIKE wildcards (% and _) and the escape char itself so a
    // user-supplied substring is matched literally under `ESCAPE '\'`.
    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");
}
