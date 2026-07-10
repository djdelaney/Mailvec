using Microsoft.Data.Sqlite;

namespace Mailvec.Core.Data;

public sealed record SyncStateEntry(string MaildirFullPath, string? MessageId, DateTimeOffset LastSeenAt, string? ContentHash, string? Folder = null);

public sealed class SyncStateRepository(ConnectionFactory connections)
{
    /// <summary>
    /// Returns the entry for a single Maildir file path, or null if none.
    /// The scanner calls this to short-circuit re-parsing of files whose mtime
    /// hasn't changed since last scan — important once the corpus has many
    /// PDFs/DOCX, since attachment-text extraction during parse is expensive.
    ///
    /// Caller-owned connection + transaction: the scanner runs this once per
    /// file (~82K per scan on real corpora), so threading the connection
    /// avoids the per-Open extension-load + PRAGMA overhead that dominated
    /// indexer CPU when this used its own connection internally.
    /// </summary>
    public SyncStateEntry? Get(SqliteConnection conn, SqliteTransaction tx, string maildirFullPath)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT maildir_full_path, message_id, last_seen_at, content_hash, folder
            FROM sync_state
            WHERE maildir_full_path = $path
            """;
        cmd.Parameters.AddWithValue("$path", maildirFullPath);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new SyncStateEntry(
            MaildirFullPath: reader.GetString(0),
            MessageId: reader.IsDBNull(1) ? null : reader.GetString(1),
            LastSeenAt: DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
            ContentHash: reader.IsDBNull(3) ? null : reader.GetString(3),
            Folder: reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    // `folder` has no default on purpose: sync_state doubles as the
    // folder-membership table for search (SearchFilterSql's EXISTS probe and
    // FolderStats both read it), so every writer must supply the copy's folder
    // or membership silently drifts NULL and folder filters stop matching.
    public void Upsert(SqliteConnection conn, SqliteTransaction tx, string maildirFullPath, string? messageId, DateTimeOffset lastSeenAt, string? contentHash, string? folder)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO sync_state (maildir_full_path, message_id, last_seen_at, content_hash, folder)
            VALUES ($path, $mid, $seen, $hash, $folder)
            ON CONFLICT(maildir_full_path) DO UPDATE SET
                message_id   = excluded.message_id,
                last_seen_at = excluded.last_seen_at,
                content_hash = excluded.content_hash,
                folder       = excluded.folder;
            """;
        cmd.Parameters.AddWithValue("$path", maildirFullPath);
        cmd.Parameters.AddWithValue("$mid", (object?)messageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$seen", lastSeenAt.ToString("O"));
        cmd.Parameters.AddWithValue("$hash", (object?)contentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$folder", (object?)folder ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SyncStateEntry> StaleEntries(DateTimeOffset olderThan)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT maildir_full_path, message_id, last_seen_at, content_hash, folder
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
                ContentHash: reader.IsDBNull(3) ? null : reader.GetString(3),
                Folder: reader.IsDBNull(4) ? null : reader.GetString(4)));
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

    /// <summary>
    /// The most recently seen live path for a Message-ID (fresh as of the
    /// cutoff), or null. Used by the scanner's reconciliation to repair a
    /// messages row that still points at a just-deleted duplicate copy.
    /// </summary>
    public string? FreshPathForMessageId(string messageId, DateTimeOffset since)
    {
        using var conn = connections.Open();
        return FreshPathForMessageId(conn, messageId, since);
    }

    /// <summary>Connection-reusing overload for the scanner's per-entry repair loop.</summary>
    public string? FreshPathForMessageId(SqliteConnection conn, string messageId, DateTimeOffset since)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT maildir_full_path FROM sync_state
            WHERE message_id = $mid AND last_seen_at >= $cutoff
            ORDER BY last_seen_at DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$mid", messageId);
        cmd.Parameters.AddWithValue("$cutoff", since.ToString("O"));
        return cmd.ExecuteScalar() as string;
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
