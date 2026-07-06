using Mailvec.Core;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailvec.Indexer.Services;

public sealed class MaildirScanner(
    IOptions<IngestOptions> ingestOptions,
    MessageParser parser,
    MessageRepository messages,
    ChunkRepository chunks,
    SyncStateRepository syncState,
    ConnectionFactory connectionFactory,
    ILogger<MaildirScanner> logger)
{
    private readonly string _maildirRoot = PathExpansion.Expand(ingestOptions.Value.MaildirRoot);

    // How many fast-path sync_state writes accumulate before we commit. Smaller
    // batches mean more fsyncs (slower scan) but tighter windows for the
    // embedder's separate connection to grab the write lock; 1000 is well
    // under the empirical scan rate, so even a slow embedder poll lands
    // inside an inter-batch gap within ~milliseconds.
    private const int BatchSize = 1000;

    public sealed record ScanResult(int Seen, int Upserted, int FailedToParse, int SoftDeleted);

    /// <summary>
    /// Walks every Maildir subfolder under MaildirRoot, parses messages, and
    /// reconciles deletions: any sync_state row not refreshed during this scan
    /// is treated as a removed message.
    /// </summary>
    public ScanResult ScanAll(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_maildirRoot))
        {
            logger.LogWarning("Maildir root {Path} does not exist; nothing to scan.", _maildirRoot);
            return new ScanResult(0, 0, 0, 0);
        }

        var scanStart = DateTimeOffset.UtcNow;
        var seen = 0;
        var upserted = 0;
        var failed = 0;
        var unrefreshed = 0;
        // Directories we couldn't enumerate (permissions, I/O). Any skipped
        // directory means the files inside it never got their sync_state
        // refreshed this scan, so — like `unrefreshed` — it must veto the
        // deletion-reconciliation pass or every message in that directory
        // would be soft-deleted as "stale".
        var enumerationFailures = 0;

        // One connection + a rolling transaction for the whole file walk.
        // The previous design opened a fresh connection for every Get/Upsert
        // (~165K Open()s per scan on an 82K-message corpus); each Open
        // reloaded vec0 and ran the PRAGMA setup, which was the dominant
        // indexer CPU sink during steady-state scans. The rolling tx commits
        // every BatchSize fast-path writes so the embedder's separate
        // connection still gets regular write-lock windows.
        using var conn = connectionFactory.Open();
        using var ctx = new ScanContext(conn, BatchSize);

        try
        {
            foreach (var folderDir in EnumerateMaildirFolders(_maildirRoot, onEnumerationError: (dir, ex) =>
            {
                logger.LogWarning(ex, "Cannot enumerate {Path}; skipping this directory.", dir);
                enumerationFailures++;
            }))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var folderName = MaildirPaths.FolderNameFor(_maildirRoot, folderDir);
                foreach (var subdir in new[] { "new", "cur" })
                {
                    var sub = Path.Combine(folderDir, subdir);
                    if (!Directory.Exists(sub)) continue;

                    // Eager GetFiles (not the lazy Enumerate) so a permission
                    // error surfaces here, where it can be scoped to this one
                    // directory instead of aborting the whole scan mid-walk.
                    string[] files;
                    try
                    {
                        files = Directory.GetFiles(sub);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        logger.LogWarning(ex, "Cannot enumerate {Path}; skipping this directory.", sub);
                        enumerationFailures++;
                        continue;
                    }

                    foreach (var file in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        seen++;

                        switch (TryIngest(ctx, file, folderName, scanStart))
                        {
                            case IngestOutcome.Ok:
                                upserted++;
                                break;
                            case IngestOutcome.Failed:
                                failed++;
                                break;
                            case IngestOutcome.FailedAndUnrefreshed:
                                failed++;
                                unrefreshed++;
                                break;
                        }
                    }
                }
            }
            ctx.Flush();
        }
        catch
        {
            ctx.Abandon();
            throw;
        }

        if (unrefreshed > 0 || enumerationFailures > 0)
        {
            // At least one live file's sync_state row could not be refreshed
            // (its ingest + refresh both failed, or its whole directory
            // couldn't be enumerated), so the deletion-reconciliation pass
            // would see it as stale and soft-delete a message whose file is
            // alive on disk. Skip reconciliation entirely this scan —
            // genuinely deleted files are simply caught one scan later.
            logger.LogWarning(
                "MaildirScanner: {Unrefreshed} file(s) failed ingest+refresh and {EnumFailures} director(ies) could not be enumerated; " +
                "skipping deletion reconciliation this scan to avoid soft-deleting live messages. " +
                "seen={Seen} upserted={Upserted} parseFailed={Failed}",
                unrefreshed, enumerationFailures, seen, upserted, failed);
            return new ScanResult(seen, upserted, failed, 0);
        }

        var stale = syncState.StaleEntries(olderThan: scanStart);

        if (seen == 0 && stale.Count > 0)
        {
            // The walk found ZERO files while sync_state still tracks some.
            // That's far more likely a vanished Maildir (network mount racing
            // up, MaildirRoot re-pointed, mbsync mid-re-init) than a genuine
            // "the user deleted every message" — and reconciling would
            // soft-delete the ENTIRE archive in one scan, at the cost of a
            // full re-parse + attachment re-extraction to recover. Skip
            // reconciliation; a genuinely emptied mailbox reconciles on the
            // first scan that sees at least one file.
            logger.LogWarning(
                "MaildirScanner: saw 0 files under {Root} but sync_state tracks {Tracked} — " +
                "skipping deletion reconciliation (empty/vanished Maildir root?). " +
                "If the mailbox was genuinely emptied, reconciliation resumes when any file appears.",
                _maildirRoot, stale.Count);
            return new ScanResult(0, upserted, failed, 0);
        }
        // A message-id with a fresh sync_state row (this scan's pass) was just
        // re-seen at a new path — treat it as a rename, not a deletion.
        var freshMessageIds = syncState.FreshMessageIds(since: scanStart);
        var staleByMessageId = stale
            .Where(e => e.MessageId is not null && !freshMessageIds.Contains(e.MessageId))
            .Select(e => e.MessageId!)
            .Distinct()
            .ToList();

        var softDeleted = 0;
        if (staleByMessageId.Count > 0)
        {
            var idsToMark = new List<long>(staleByMessageId.Count);
            foreach (var mid in staleByMessageId)
            {
                var msg = messages.GetByMessageId(mid);
                if (msg is { DeletedAt: null }) idsToMark.Add(msg.Id);
            }
            if (idsToMark.Count > 0)
            {
                softDeleted = messages.MarkDeleted(idsToMark, scanStart);
            }
        }

        // Stale entries whose Message-ID IS fresh are renames or deleted
        // duplicate copies — the message stays live via another path. But the
        // messages row may still point at the path that just vanished: the
        // surviving copy rides the mtime fast-path and never re-upserts, so
        // the dangling path would persist forever (get_attachment fails, the
        // OCR pass re-selects and skips those attachments every cycle).
        // Repoint the row at a live fresh path for the same Message-ID.
        var repaired = 0;
        foreach (var entry in stale)
        {
            if (entry.MessageId is null || !freshMessageIds.Contains(entry.MessageId)) continue;

            var msg = messages.GetByMessageId(entry.MessageId);
            if (msg is null || msg.DeletedAt is not null) continue;

            var currentAbs = Path.Combine(_maildirRoot, msg.MaildirPath, msg.MaildirFilename);
            if (!string.Equals(Path.GetFullPath(currentAbs), Path.GetFullPath(entry.MaildirFullPath), StringComparison.Ordinal))
                continue; // row already points at a different (live) copy

            var freshPath = syncState.FreshPathForMessageId(entry.MessageId, since: scanStart);
            if (freshPath is null) continue;

            var folderDir = Path.GetDirectoryName(Path.GetDirectoryName(freshPath));
            if (folderDir is null) continue;
            messages.UpdateMaildirLocation(
                msg.Id,
                MaildirPaths.FolderNameFor(_maildirRoot, folderDir),
                MaildirPaths.RelativeFolderPath(_maildirRoot, freshPath),
                Path.GetFileName(freshPath));
            repaired++;
        }
        if (repaired > 0)
        {
            logger.LogInformation("MaildirScanner: repointed {Count} message(s) from a deleted duplicate copy to a live path.", repaired);
        }

        if (stale.Count > 0)
        {
            syncState.Remove(stale.Select(e => e.MaildirFullPath));
        }

        logger.LogInformation(
            "MaildirScanner: seen={Seen} upserted={Upserted} parseFailed={Failed} softDeleted={SoftDeleted}",
            seen, upserted, failed, softDeleted);

        return new ScanResult(seen, upserted, failed, softDeleted);
    }

    private enum IngestOutcome
    {
        Ok,
        Failed,
        /// <summary>
        /// Ingest failed AND the catch handler's sync_state refresh also
        /// failed — this file's row is now stale even though the file is
        /// alive, so the caller must skip deletion reconciliation.
        /// </summary>
        FailedAndUnrefreshed,
    }

    private IngestOutcome TryIngest(ScanContext ctx, string filePath, string folderName, DateTimeOffset indexedAt)
    {
        // Hoisted so the catch below can preserve the existing message_id /
        // content_hash instead of nulling them (see the catch comment).
        SyncStateEntry? prior = null;
        try
        {
            // Fast path: if sync_state remembers this exact path AND the file
            // hasn't been modified since the last scan recorded it, the parse
            // would just rebuild the same ParsedMessage we already have on
            // disk. Skip the parse entirely (PDF / DOCX text extraction is
            // expensive) and just refresh last_seen_at so the deletion-
            // reconciliation pass doesn't soft-delete it. Mbsync flag rewrites
            // bump mtime, so the optimization is robust against IMAP flag
            // changes that don't actually mutate body content.
            //
            // ContentHash must be non-null to take the fast path: a NULL hash
            // is the "last ingest attempt failed" marker written by the catch
            // below. Without it, a single transient failure (SQLITE_BUSY, I/O
            // blip) on a *changed* file would stamp last_seen_at past the
            // file's mtime and the change would be skipped on every future
            // scan — permanently masking the new content.
            prior = syncState.Get(ctx.Connection, ctx.Transaction, filePath);
            if (prior is { MessageId: not null, ContentHash: not null }
                && File.GetLastWriteTimeUtc(filePath) <= prior.LastSeenAt.UtcDateTime)
            {
                syncState.Upsert(ctx.Connection, ctx.Transaction, filePath, prior.MessageId, indexedAt, prior.ContentHash, folderName);
                ctx.NoteWrite();
                return IngestOutcome.Ok;
            }

            // Parse path: messages.Upsert opens its own connection. Release
            // our held write lock first by committing any batched fast-path
            // writes — otherwise its tx will block on busy_timeout against
            // ours (single-writer in WAL mode).
            ctx.Flush();

            var parsed = parser.ParseFile(filePath);
            var relPath = MaildirPaths.RelativeFolderPath(_maildirRoot, filePath);
            var fileName = Path.GetFileName(filePath);

            var outcome = messages.Upsert(parsed, folderName, relPath, fileName, indexedAt);
            if (outcome.ContentChanged)
            {
                // Body bytes mutated upstream — drop the chunks and vectors
                // built from the old body_text so the embedder regenerates
                // them against the new content. body_text/FTS already updated
                // by the upsert + FTS5 triggers.
                chunks.ClearEmbeddingsForMessage(outcome.Id);
                logger.LogInformation(
                    "Content changed for message_id={MessageId} (id={Id}); cleared embeddings.",
                    parsed.MessageId, outcome.Id);
            }
            syncState.Upsert(ctx.Connection, ctx.Transaction, filePath, parsed.MessageId, indexedAt, parsed.ContentHash, folderName);
            ctx.NoteWrite();
            return IngestOutcome.Ok;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse {Path}", filePath);
            // Refresh last_seen_at so we don't treat the file as deleted next
            // pass, but PRESERVE the prior message_id. Nulling message_id
            // would drop this path out of the deletion-reconciliation mapping
            // (it filters on message_id != null), so if the file is later
            // removed, its message would be stranded "live" forever.
            //
            // content_hash is deliberately NULLed as a "retry me" marker: the
            // mtime fast path requires a non-null hash, so the next scan
            // re-parses this file instead of trusting the fresh last_seen_at
            // stamp. Preserving the prior hash here would let a transient
            // failure on a changed file silently mask the change forever.
            try
            {
                syncState.Upsert(ctx.Connection, ctx.Transaction, filePath, prior?.MessageId, indexedAt, contentHash: null, folderName);
                ctx.NoteWrite();
            }
            catch (Exception refreshEx)
            {
                // The row is now stale even though the file exists — surface
                // it so ScanAll skips deletion reconciliation this scan
                // instead of soft-deleting a live message.
                logger.LogWarning(refreshEx, "Also failed to refresh sync_state for {Path}", filePath);
                return IngestOutcome.FailedAndUnrefreshed;
            }
            return IngestOutcome.Failed;
        }
    }

    /// <summary>
    /// A Maildir folder is any directory that itself contains the canonical
    /// new/ and cur/ subdirectories. Walks recursively so nested folders
    /// (e.g. Archive.2024) are picked up. A directory that can't be listed
    /// (permissions, I/O — think a TCC-protected or cloud-placeholder dir
    /// someone dropped under the Maildir root) is reported via
    /// <paramref name="onEnumerationError"/> and skipped rather than aborting
    /// the walk: one bad directory must not stop the whole archive from
    /// indexing, and under launchd KeepAlive a throw here at startup becomes
    /// a permanent crash-restart loop.
    /// </summary>
    private IEnumerable<string> EnumerateMaildirFolders(string root, Action<string, Exception> onEnumerationError)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            if (Directory.Exists(Path.Combine(dir, "cur")) || Directory.Exists(Path.Combine(dir, "new")))
            {
                yield return dir;
            }

            // Eager GetDirectories: a yield-iterator can't catch around a
            // lazy enumerator's MoveNext without losing the rest of the walk.
            string[] subs;
            try
            {
                subs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                onEnumerationError(dir, ex);
                continue;
            }

            foreach (var sub in subs)
            {
                var leaf = Path.GetFileName(sub);
                // Skip the maildir-internal subdirs themselves so they aren't reported as folders.
                if (leaf is "new" or "cur" or "tmp")
                {
                    // ...but an IMAP folder LITERALLY named "tmp"/"new"/"cur"
                    // is indistinguishable from the internals by name alone.
                    // If the skipped dir itself has cur/new children, it's a
                    // real Maildir folder whose mail will silently never be
                    // indexed — say so instead of staying quiet. (Renaming
                    // the folder server-side is the fix; supporting those
                    // names would need depth-aware skipping and a watcher
                    // change — see MaildirWatcher's tmp filter.)
                    if (Directory.Exists(Path.Combine(sub, "cur")) || Directory.Exists(Path.Combine(sub, "new")))
                    {
                        logger.LogWarning(
                            "MaildirScanner: {Path} looks like a real mail folder but is named '{Leaf}' " +
                            "(a Maildir-internal name) — its messages will NOT be indexed. Rename the IMAP folder to fix.",
                            sub, leaf);
                    }
                    continue;
                }
                stack.Push(sub);
            }
        }
    }
}

/// <summary>
/// Scoped helper that owns the scanner's single connection and a rolling
/// transaction. Each fast-path sync_state write is recorded via
/// <see cref="NoteWrite"/>; once <see cref="_batchSize"/> writes have accumulated,
/// the tx auto-commits and a fresh one is begun. The parse path calls
/// <see cref="Flush"/> before invoking repositories on their own connections
/// (e.g. MessageRepository.Upsert) to avoid blocking on the write lock.
///
/// NOTE: Microsoft.Data.Sqlite's BeginTransaction() issues BEGIN IMMEDIATE
/// (its `deferred` parameter defaults to false in ≥5.0), so the write lock
/// is taken at the FIRST statement in the tx and held until Flush(). That is
/// why the parse path in TryIngest MUST call Flush() before invoking
/// MessageRepository.Upsert — Upsert opens its own connection, and with our
/// lock still held its BEGIN IMMEDIATE would block for the full busy_timeout
/// on every parsed file. Don't remove that Flush(), and don't assume an open
/// tx here is lock-free.
/// </summary>
internal sealed class ScanContext : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly int _batchSize;
    private SqliteTransaction? _tx;
    private int _writesInTx;

    public ScanContext(SqliteConnection connection, int batchSize)
    {
        _connection = connection;
        _batchSize = batchSize;
    }

    public SqliteConnection Connection => _connection;

    public SqliteTransaction Transaction => _tx ??= _connection.BeginTransaction();

    public void NoteWrite()
    {
        if (++_writesInTx >= _batchSize) Flush();
    }

    public void Flush()
    {
        if (_tx is null) return;
        _tx.Commit();
        _tx.Dispose();
        _tx = null;
        _writesInTx = 0;
    }

    public void Abandon()
    {
        if (_tx is null) return;
        try { _tx.Rollback(); } catch { /* connection may already be closed */ }
        _tx.Dispose();
        _tx = null;
        _writesInTx = 0;
    }

    public void Dispose()
    {
        // Defensive: if Flush() ran in the happy path this is a no-op.
        // If we got here via an unhandled exception (caller forgot to call
        // Abandon), roll back so the connection is returned to the pool clean.
        Abandon();
    }
}

internal static class MaildirPaths
{
    public static string FolderNameFor(string root, string folderDir)
    {
        var rel = Path.GetRelativePath(root, folderDir);
        if (rel == ".") return "INBOX";
        // mbsync's "Subfolders Verbatim" uses dot-separated names like "Archive.2024".
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// Relative directory path including the new/cur leaf, e.g. "INBOX/cur" or "Archive.2024/new".
    /// </summary>
    public static string RelativeFolderPath(string root, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath)!;
        var rel = Path.GetRelativePath(root, dir);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }
}
