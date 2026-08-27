using Microsoft.Data.Sqlite;

namespace Mailvec.Core.Data;

public sealed record SyncStateEntry(
    string MaildirFullPath,
    string? MessageId,
    // When this ROW was last written — NOT when the file was last seen, despite
    // the column name (renaming it would cost a migration for no behavioural
    // gain). A scan that walks a file and finds it unchanged writes nothing, so
    // a live file's row can carry a timestamp from weeks ago. Nothing outside
    // the scanner reads it, and the scanner uses it only to order candidate
    // paths in PathsForMessageId and as the pre-009 identity fallback (which
    // every row leaves behind after one scan). Deletion detection is NOT based
    // on it — see EntriesNotObserved.
    DateTimeOffset LastSeenAt,
    string? ContentHash,
    string? Folder = null,
    // The file identity observed at the last ingest. NULL on rows written
    // before v9 (and on the legacy fallback path) — the scanner treats NULL as
    // "no recorded identity" rather than a match. See migration 009.
    DateTimeOffset? FileMtimeUtc = null,
    long? FileSize = null);

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
    /// <remarks>
    /// <paramref name="tx"/> is nullable and callers should pass the
    /// transaction that is ALREADY open, never one begun on demand. This is a
    /// read; beginning a transaction for it takes SQLite's single writer slot
    /// (Microsoft.Data.Sqlite issues BEGIN IMMEDIATE) and the scanner calls
    /// this once per file, so a lazily-created transaction here is held across
    /// the whole walk. Passing null when none is open runs the read on its own
    /// implicit transaction, which is what a scan wants — it has no need of a
    /// consistent snapshot across the walk, and never had one.
    /// </remarks>
    public SyncStateEntry? Get(SqliteConnection conn, SqliteTransaction? tx, string maildirFullPath)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT maildir_full_path, message_id, last_seen_at, content_hash, folder, file_mtime_utc, file_size
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
            Folder: reader.IsDBNull(4) ? null : reader.GetString(4),
            FileMtimeUtc: reader.IsDBNull(5)
                ? null
                : DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
            FileSize: reader.IsDBNull(6) ? null : reader.GetInt64(6));
    }

    // `folder` has no default on purpose: sync_state doubles as the
    // folder-membership table for search (SearchFilterSql's EXISTS probe and
    // FolderStats both read it), so every writer must supply the copy's folder
    // or membership silently drifts NULL and folder filters stop matching.
    // `folder` and the two file-identity parameters have no defaults on
    // purpose. Omitting the identity silently writes NULL, which puts the row
    // back on the legacy comparison and reopens the preserved-mtime skip that
    // migration 009 exists to close — a caller that can't supply it should say
    // so by passing null explicitly.
    public void Upsert(
        SqliteConnection conn,
        SqliteTransaction tx,
        string maildirFullPath,
        string? messageId,
        DateTimeOffset lastSeenAt,
        string? contentHash,
        string? folder,
        DateTimeOffset? fileMtimeUtc,
        long? fileSize)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO sync_state (maildir_full_path, message_id, last_seen_at, content_hash, folder, file_mtime_utc, file_size)
            VALUES ($path, $mid, $seen, $hash, $folder, $mtime, $size)
            ON CONFLICT(maildir_full_path) DO UPDATE SET
                message_id     = excluded.message_id,
                last_seen_at   = excluded.last_seen_at,
                content_hash   = excluded.content_hash,
                folder         = excluded.folder,
                file_mtime_utc = excluded.file_mtime_utc,
                file_size      = excluded.file_size;
            """;
        cmd.Parameters.AddWithValue("$path", maildirFullPath);
        cmd.Parameters.AddWithValue("$mid", (object?)messageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$seen", lastSeenAt.ToString("O"));
        cmd.Parameters.AddWithValue("$hash", (object?)contentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$folder", (object?)folder ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mtime", (object?)fileMtimeUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$size", (object?)fileSize ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// How many paths sync_state currently tracks. The scanner uses it only to
    /// pre-size the set it collects the walk into — an estimate is fine, and a
    /// stale one costs at most some rehashing.
    /// </summary>
    public int TrackedPathCount()
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sync_state";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Tracked paths the caller did NOT observe on its walk — the scanner's
    /// deletion-reconciliation candidates.
    /// </summary>
    /// <remarks>
    /// <para>This replaces a <c>WHERE last_seen_at &lt; $scanStart</c> query, and
    /// the swap is what lets an unchanged scan write nothing at all. Under the
    /// timestamp scheme "we still see this file" was expressed by RE-STAMPING
    /// the row, so proving 82K files were alive cost 82K row rewrites — one
    /// SQLite page each, every scan, forever (measured at 467 GB/day on the
    /// author's deployment). The set of paths the walk just enumerated carries
    /// exactly the same information and costs nothing to write down.</para>
    ///
    /// <para>The semantics are deliberately identical to what the cutoff query
    /// meant: <b>tracked but not walked this scan</b>. Not "tracked but absent
    /// from disk" — a file the walk never reached (an unreadable directory, a
    /// folder whose name collides with a Maildir internal) must keep counting
    /// as unwalked, because that is the condition the caller's reconciliation
    /// veto is written against. Swapping in a <c>File.Exists</c> probe here
    /// would quietly change which of those cases soft-deletes.</para>
    ///
    /// <para>Filtering happens while the reader streams, so the returned list
    /// holds only the misses — zero rows on a steady-state scan — rather than
    /// materialising the whole table. The scan is a full pass over
    /// <c>sync_state</c> either way: <c>last_seen_at</c> has no index, so the
    /// query this replaces was also a full scan.</para>
    /// </remarks>
    public IReadOnlyList<SyncStateEntry> EntriesNotObserved(IReadOnlySet<string> observedPaths)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT maildir_full_path, message_id, last_seen_at, content_hash, folder
            FROM sync_state
            """;

        var list = new List<SyncStateEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            if (observedPaths.Contains(path)) continue;
            list.Add(new SyncStateEntry(
                MaildirFullPath: path,
                MessageId: reader.IsDBNull(1) ? null : reader.GetString(1),
                LastSeenAt: DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
                ContentHash: reader.IsDBNull(3) ? null : reader.GetString(3),
                Folder: reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return list;
    }

    /// <summary>
    /// Every tracked path recorded for a Message-ID, most recently written
    /// first. The scanner intersects this with the paths it actually walked to
    /// answer both halves of one question about a vanished copy: does the
    /// message survive somewhere else (a rename, or one copy of a multi-folder
    /// message being removed), and if so, which live path should the messages
    /// row be repointed at?
    /// </summary>
    /// <remarks>
    /// Replaces a <c>FreshMessageIds</c> set built from
    /// <c>last_seen_at &gt;= $scanStart</c> plus a per-entry
    /// <c>FreshPathForMessageId</c>. Both read freshness out of a column the
    /// scanner no longer restamps on an unchanged file, so both would now read
    /// every surviving copy as absent and soft-delete live mail. Asking for the
    /// copies by Message-ID and letting the caller test them against the walk
    /// is the same question without the timestamp.
    ///
    /// <para>Called once per stale entry rather than once per scan — normally
    /// zero times, and the <c>(message_id, folder)</c> index makes each lookup
    /// a point query even when a bulk server-side archive stales thousands at
    /// once. Ordering by <c>last_seen_at</c> preserves the old
    /// most-recently-written preference when a message has several live
    /// copies.</para>
    /// </remarks>
    public IReadOnlyList<string> PathsForMessageId(SqliteConnection conn, string messageId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT maildir_full_path FROM sync_state
            WHERE message_id = $mid
            ORDER BY last_seen_at DESC
            """;
        cmd.Parameters.AddWithValue("$mid", messageId);

        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    /// <summary>
    /// Drop the recorded content_hash for one path, forcing the next scan to
    /// re-parse it instead of taking the mtime fast path (which requires a
    /// non-null hash — the same "retry me" marker a failed ingest writes).
    /// </summary>
    /// <remarks>
    /// Load-bearing for the scanner's rename-repair pass. Once a message row is
    /// repointed at a surviving copy, its stored body/attachment metadata still
    /// came from the copy that just vanished — and <c>MessageRepository.Upsert</c>
    /// only accepts content from the attributed copy, so without this the
    /// survivor would ride the fast path forever and the row would never
    /// re-align. That also covers a rename that changed the file's content:
    /// the parse at the new path isn't yet attributed, so its change would
    /// otherwise be recorded in sync_state and never applied to the message.
    /// </remarks>
    /// <summary>
    /// Clears the recorded content hash so the next scan re-parses this file
    /// (the mtime fast path requires a non-null hash). Pass <paramref name="tx"/>
    /// to enlist in the caller's transaction — the scanner's rename repair
    /// does, because this write and the row repoint must land together or not
    /// at all. See <c>MessageRepository.UpdateMaildirLocation</c>'s overload.
    /// </summary>
    public void ClearContentHash(SqliteConnection conn, string maildirFullPath, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE sync_state SET content_hash = NULL WHERE maildir_full_path = $path";
        cmd.Parameters.AddWithValue("$path", maildirFullPath);
        cmd.ExecuteNonQuery();
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
