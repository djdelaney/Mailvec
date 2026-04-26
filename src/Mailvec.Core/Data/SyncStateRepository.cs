using Microsoft.Data.Sqlite;

namespace Mailvec.Core.Data;

public sealed record SyncStateEntry(string MaildirFullPath, string? MessageId, DateTimeOffset LastSeenAt, string? ContentHash);

public sealed class SyncStateRepository(ConnectionFactory connections)
{
    public void Upsert(string maildirFullPath, string? messageId, DateTimeOffset lastSeenAt, string? contentHash = null)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_state (maildir_full_path, message_id, last_seen_at, content_hash)
            VALUES ($path, $mid, $seen, $hash)
            ON CONFLICT(maildir_full_path) DO UPDATE SET
                message_id   = excluded.message_id,
                last_seen_at = excluded.last_seen_at,
                content_hash = excluded.content_hash;
            """;
        cmd.Parameters.AddWithValue("$path", maildirFullPath);
        cmd.Parameters.AddWithValue("$mid", (object?)messageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$seen", lastSeenAt.ToString("O"));
        cmd.Parameters.AddWithValue("$hash", (object?)contentHash ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SyncStateEntry> StaleEntries(DateTimeOffset olderThan)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT maildir_full_path, message_id, last_seen_at, content_hash
            FROM sync_state
            WHERE last_seen_at < $cutoff
            """;
        cmd.Parameters.AddWithValue("$cutoff", olderThan.ToString("O"));

        var list = new List<SyncStateEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new SyncStateEntry(
                MaildirFullPath: reader.GetString(0),
                MessageId: reader.IsDBNull(1) ? null : reader.GetString(1),
                LastSeenAt: DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
                ContentHash: reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return list;
    }

    /// <summary>
    /// Message-IDs whose sync_state row was refreshed at or after the cutoff.
    /// Used by the scanner to distinguish "file was renamed" (still has a
    /// fresh row at a new path) from "file was deleted" (no fresh row).
    /// </summary>
    public HashSet<string> FreshMessageIds(DateTimeOffset since)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT message_id
            FROM sync_state
            WHERE last_seen_at >= $cutoff AND message_id IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("$cutoff", since.ToString("O"));

        var set = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) set.Add(reader.GetString(0));
        return set;
    }

    public int Remove(IEnumerable<string> maildirFullPaths)
    {
        using var conn = connections.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM sync_state WHERE maildir_full_path = $path";
        var p = cmd.Parameters.Add("$path", SqliteType.Text);

        var affected = 0;
        foreach (var path in maildirFullPaths)
        {
            p.Value = path;
            affected += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return affected;
    }
}
