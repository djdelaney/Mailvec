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

    /// <summary>
    /// Test seam: invoked once per walked file, after its ingest attempt.
    /// Exists so a test can probe for SQLite's writer lock from an independent
    /// connection while a scan is mid-walk — the one property of this loop that
    /// cannot be observed from its return value or from the database afterwards.
    /// </summary>
    internal Action? OnFileWalked { get; set; }

    /// <summary>
    /// Test seam: how many transactions the last <see cref="ScanAll"/> BEGAN.
    /// On a scan over an unchanged corpus this must be ZERO.
    /// </summary>
    /// <remarks>
    /// Bounding how LONG the writer lock is held is not the same as not taking
    /// it. A read that reaches for the transaction-creating property issues
    /// BEGIN IMMEDIATE per file and commits it again moments later, so an idle
    /// scan acquires and releases SQLite's single writer slot once per message
    /// — 82K times a minute on a real corpus — while a probe between files sees
    /// it free and the WAL stays empty (an empty transaction dirties no pages).
    /// Neither the lock probe nor the zero-WAL assertion can see that; counting
    /// the BEGINs can.
    /// </remarks>
    internal int LastScanTransactionsBegun { get; private set; }

    /// <summary>
    /// <paramref name="Seen"/> is every file the walk enumerated.
    /// <paramref name="Upserted"/> counts only the files that were actually
    /// parsed and written through <c>MessageRepository.Upsert</c>;
    /// <paramref name="Unchanged"/> counts the files the fast path recognised
    /// and skipped. Seen == Upserted + Unchanged + FailedToParse.
    /// </summary>
    /// <remarks>
    /// Upserted used to include the fast-path skips, which made it read
    /// <c>upserted == seen</c> on every steady-state scan and cost a real
    /// investigation into a full-corpus re-parse that was never happening. The
    /// number that was genuinely proportional to the corpus was the WRITE
    /// volume, not the parse volume, and the counter pointed away from it.
    /// </remarks>
    public sealed record ScanResult(int Seen, int Upserted, int Unchanged, int FailedToParse, int SoftDeleted);

    /// <summary>
    /// Walks every Maildir subfolder under MaildirRoot, parses messages, and
    /// reconciles deletions: any sync_state row whose path this scan did not
    /// walk is treated as a removed message.
    /// </summary>
    /// <remarks>
    /// A scan over an unchanged corpus writes NOTHING. Every file takes the
    /// fast path in <see cref="TryIngest"/>, which stats the file, matches it
    /// against the recorded identity and returns without touching the
    /// database; liveness is carried by the in-memory observedPaths set rather
    /// than by re-stamping each row. Keep it that way — this loop runs once a
    /// minute over the whole corpus, so any per-file write added here is
    /// multiplied by the corpus size and by 1440.
    /// </remarks>
    public ScanResult ScanAll(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_maildirRoot))
        {
            logger.LogWarning("Maildir root {Path} does not exist; nothing to scan.", _maildirRoot);
            return new ScanResult(0, 0, 0, 0, 0);
        }

        var scanStart = DateTimeOffset.UtcNow;
        var seen = 0;
        var upserted = 0;
        var unchanged = 0;
        var failed = 0;
        var unrefreshed = 0;
        // Every Maildir file this walk enumerated. This is the deletion
        // reconciliation input, replacing the last_seen_at restamp that used
        // to record the same fact one row-write at a time (see
        // SyncStateRepository.EntriesNotObserved).
        //
        // A path is added the moment the walk reaches it, BEFORE the ingest is
        // attempted, so a parse failure cannot make a live file look deleted —
        // that used to depend on the catch handler's sync_state refresh
        // succeeding, which is why IngestOutcome still carries
        // FailedAndUnrefreshed.
        //
        // Ordinal, matching sync_state's BINARY primary key and the fact that
        // these strings and the stored ones come from the same walk. Sized
        // from the tracked-row count so the steady-state scan does not rehash
        // its way up from 16 buckets every minute. Costs roughly 15 MB at the
        // author's 82K-message corpus and grows linearly; the alternative,
        // probing File.Exists per tracked row at reconciliation time, trades
        // that for a second stat() of the whole corpus per scan AND changes
        // which unwalked-but-present files soft-delete.
        var observedPaths = new HashSet<string>(syncState.TrackedPathCount(), StringComparer.Ordinal);
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
        LastScanTransactionsBegun = 0;

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
                        observedPaths.Add(file);

                        switch (TryIngest(ctx, file, folderName, scanStart))
                        {
                            case IngestOutcome.Upserted:
                                upserted++;
                                break;
                            case IngestOutcome.Unchanged:
                                unchanged++;
                                break;
                            case IngestOutcome.Failed:
                                failed++;
                                break;
                            case IngestOutcome.FailedAndUnrefreshed:
                                failed++;
                                unrefreshed++;
                                break;
                        }

                        OnFileWalked?.Invoke();
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
        LastScanTransactionsBegun = ctx.TransactionsBegun;

        if (unrefreshed > 0 || enumerationFailures > 0)
        {
            // A directory that could not be enumerated is the load-bearing
            // half: its files were never walked, so they are absent from
            // observedPaths and reconciliation would soft-delete every message
            // in that directory while the files sit alive on disk. Skip
            // reconciliation entirely — genuinely deleted files are caught one
            // scan later.
            //
            // `unrefreshed` no longer implies that hazard. Under the
            // last_seen_at scheme a live file's row went stale whenever its
            // catch-handler refresh failed to WRITE; now the walk records the
            // path in observedPaths before the ingest is even attempted, so a
            // failed refresh cannot make the file look deleted. It is kept as
            // a veto anyway: a sync_state write failing at all means the
            // scan's connection is in trouble, which is the wrong moment to
            // start soft-deleting rows on the strength of what it just read.
            logger.LogWarning(
                "MaildirScanner: {Unrefreshed} file(s) failed ingest+refresh and {EnumFailures} director(ies) could not be enumerated; " +
                "skipping deletion reconciliation this scan to avoid soft-deleting live messages. " +
                "seen={Seen} upserted={Upserted} unchanged={Unchanged} parseFailed={Failed}",
                unrefreshed, enumerationFailures, seen, upserted, unchanged, failed);
            return new ScanResult(seen, upserted, unchanged, failed, 0);
        }

        // Tracked paths this walk never enumerated. Under the old scheme this
        // was `last_seen_at < scanStart`, which is why every seen file had to
        // be restamped; observedPaths carries the identical fact for free.
        var stale = syncState.EntriesNotObserved(observedPaths);

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
            return new ScanResult(0, upserted, unchanged, failed, 0);
        }

        // Sort each vanished path into "the message is gone" and "the message
        // survives at another path this scan walked" (an mbsync new/ -> cur/
        // rename, or one copy of a multi-folder message being removed).
        //
        // The survivor test is `another recorded path for this Message-ID that
        // the walk just enumerated`. It used to be `another sync_state row for
        // this Message-ID stamped at or after scanStart` — the same question
        // asked of last_seen_at, which the fast path no longer restamps. Left
        // alone, that would have read every surviving copy as absent and soft-
        // deleted the live message on the first scan after this change.
        var deletedMessageIds = new List<string>();
        var repointCandidates = new List<(SyncStateEntry Entry, string SurvivingPath)>();
        foreach (var entry in stale)
        {
            if (entry.MessageId is null) continue;
            cancellationToken.ThrowIfCancellationRequested();

            var survivingPath = syncState
                .PathsForMessageId(conn, entry.MessageId)
                .FirstOrDefault(p =>
                    !string.Equals(p, entry.MaildirFullPath, StringComparison.Ordinal)
                    && observedPaths.Contains(p));

            if (survivingPath is null) deletedMessageIds.Add(entry.MessageId);
            else repointCandidates.Add((entry, survivingPath));
        }
        // Distinct: a message with several vanished copies must not be
        // looked up (or marked) once per copy.
        var goneMessageIds = deletedMessageIds.Distinct().ToList();

        var softDeleted = 0;
        if (goneMessageIds.Count > 0)
        {
            var idsToMark = new List<long>(goneMessageIds.Count);
            foreach (var mid in goneMessageIds)
            {
                // Reuse the scan's connection (ctx is flushed — no tx open):
                // a bulk server-side archive can stale thousands of entries,
                // and one fresh connection per lookup (vec0 reload + PRAGMAs)
                // stretched reconciliation by tens of seconds while the
                // coalescing channel held back the next scan.
                cancellationToken.ThrowIfCancellationRequested();
                var msg = messages.GetByMessageId(conn, mid);
                if (msg is { DeletedAt: null }) idsToMark.Add(msg.Id);
            }
            if (idsToMark.Count > 0)
            {
                softDeleted = messages.MarkDeleted(idsToMark, scanStart);
            }
        }

        // Stale entries whose message survives elsewhere are renames or
        // deleted duplicate copies — the message stays live via another path.
        // But the messages row may still point at the path that vanished: the
        // surviving copy rides the mtime fast-path and never re-upserts, so
        // the dangling path would persist forever (view_attachment fails, the
        // OCR pass re-selects and skips those attachments every cycle).
        // Repoint the row at a live fresh path for the same Message-ID.
        var repaired = 0;
        foreach (var (entry, freshPath) in repointCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var msg = messages.GetByMessageId(conn, entry.MessageId!);
            if (msg is null || msg.DeletedAt is not null) continue;

            var currentAbs = Path.Combine(_maildirRoot, msg.MaildirPath, msg.MaildirFilename);
            if (!string.Equals(Path.GetFullPath(currentAbs), Path.GetFullPath(entry.MaildirFullPath), StringComparison.Ordinal))
                continue; // row already points at a different (live) copy

            var folderDir = Path.GetDirectoryName(Path.GetDirectoryName(freshPath));
            if (folderDir is null) continue;
            // ONE transaction, because these two writes are a pair.
            //
            // Repointing moves attribution but not content: the row's body,
            // content_hash and attachment rows still came from the copy that
            // just vanished, and MessageRepository.Upsert only accepts content
            // from the attributed copy. The survivor was already walked this
            // scan, so without clearing its recorded hash it would ride the
            // mtime fast path forever and the row would never re-align.
            // Costs one re-parse of that file on the next scan.
            //
            // As two autocommit writes this had a permanent-corruption window:
            // a crash or SQLITE_BUSY after the repoint and before the clear
            // left the row pointing at the new path while holding the vanished
            // copy's content, and nothing could ever detect or repair it — the
            // survivor kept a non-null hash (so the mtime fast path skipped
            // it), and this repair pass skips any row already pointing at a
            // live copy. Both writes now commit together or neither does; a
            // failure leaves the stale sync_state entry intact (it is only
            // removed after this loop), so the next scan retries the repair.
            using (var repairTx = conn.BeginTransaction())
            {
                messages.UpdateMaildirLocation(
                    conn,
                    repairTx,
                    msg.Id,
                    MaildirPaths.FolderNameFor(_maildirRoot, folderDir),
                    MaildirPaths.RelativeFolderPath(_maildirRoot, freshPath),
                    Path.GetFileName(freshPath));
                syncState.ClearContentHash(conn, freshPath, repairTx);
                repairTx.Commit();
            }
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
            "MaildirScanner: seen={Seen} upserted={Upserted} unchanged={Unchanged} parseFailed={Failed} softDeleted={SoftDeleted}",
            seen, upserted, unchanged, failed, softDeleted);

        return new ScanResult(seen, upserted, unchanged, failed, softDeleted);
    }

    private enum IngestOutcome
    {
        /// <summary>Parsed and written through MessageRepository.Upsert.</summary>
        Upserted,
        /// <summary>
        /// Recognised by the fast path as the file we already ingested. In the
        /// steady state this is every file in the corpus and it writes nothing.
        /// </summary>
        Unchanged,
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
            // on disk is byte-for-byte the one we recorded, the parse would
            // just rebuild the same ParsedMessage. Skip it entirely (PDF /
            // DOCX text extraction is expensive) and refresh last_seen_at so
            // the deletion-reconciliation pass doesn't soft-delete the row.
            //
            // The identity test is EQUALITY against the mtime + size we
            // observed at the last ingest, not an inequality against when we
            // observed it. That distinction is the whole point: last_seen_at
            // is when Mailvec looked, so `mtime <= last_seen_at` is satisfied
            // by any file whose content changed while its mtime stayed at or
            // below the previous scan's start — and because each scan then
            // re-stamps last_seen_at later without parsing, the skip
            // self-perpetuates and the row is pinned to content that is no
            // longer on disk, permanently and silently. mbsync never produces
            // that shape (new files carry current times; flag changes are
            // renames, which land at a fresh path and miss this lookup), but
            // every restore tool does — rsync -a, cp -p and tar -x all
            // preserve mtime by design. Equality costs the same: we already
            // stat the file, and size rides along on the same call.
            //
            // ContentHash must be non-null to take the fast path: a NULL hash
            // is the "last ingest attempt failed" marker written by the catch
            // below. Without it, a single transient failure (SQLITE_BUSY, I/O
            // blip) on a *changed* file would record the new file's identity
            // as though it had been ingested, and the change would be skipped
            // on every future scan — permanently masking the new content.
            // CurrentTransaction, not Transaction: this is a read, and the
            // creating property would BEGIN IMMEDIATE on the first file and
            // hold the writer lock for the entire walk now that unchanged
            // files no longer write (and so no longer reach the NoteWrite that
            // used to commit the batch). Enlist in a transaction that is
            // already open; otherwise run outside one.
            prior = syncState.Get(ctx.Connection, ctx.CurrentTransaction, filePath);
            var info = new FileInfo(filePath);
            var mtimeUtc = info.LastWriteTimeUtc;
            var sizeBytes = info.Length;
            if (prior is { MessageId: not null, ContentHash: not null }
                && FileIdentityUnchanged(prior, mtimeUtc, sizeBytes))
            {
                // NOTHING TO WRITE. The file is the one this row already
                // describes, so the row is already correct — and re-stamping it
                // to say "still here" is what made an idle indexer rewrite the
                // entire corpus once per scan: 82K rows, one SQLite page each,
                // 467 GB/day into the WAL, which in turn kept the checkpointer
                // running continuously against the 4.5 GB main file. Liveness
                // is recorded in the caller's observedPaths set instead.
                //
                // Don't reintroduce a write here "so last_seen_at stays
                // fresh". Nothing outside this class reads that column, and
                // deletion detection no longer consults it.
                //
                // The exception is a row that is genuinely out of date in a
                // way a LATER scan or a search depends on. Both cases are
                // one-time convergence after a schema upgrade, not per-scan
                // work:
                //
                //   * file identity NULL (pre-009 row). FileIdentityUnchanged
                //     matched via its legacy `mtime <= last_seen_at` fallback,
                //     which is exactly the preserved-mtime hole 009 exists to
                //     close. Record the real identity now — otherwise, with
                //     last_seen_at also frozen, the row would sit on that
                //     fallback forever instead of converging after one scan.
                //   * folder NULL or wrong (pre-008 row). sync_state IS the
                //     folder-membership table for search, so leaving it stale
                //     silently shrinks what folder filters match.
                var rowIsCurrent =
                    prior.FileMtimeUtc is not null
                    && string.Equals(prior.Folder, folderName, StringComparison.Ordinal);
                if (!rowIsCurrent)
                {
                    syncState.Upsert(
                        ctx.Connection, ctx.Transaction, filePath, prior.MessageId, indexedAt, prior.ContentHash, folderName,
                        fileMtimeUtc: mtimeUtc, fileSize: sizeBytes);
                    ctx.NoteWrite();
                }
                else
                {
                    // This file needs no write, so there is nothing to batch —
                    // commit whatever an earlier run of writes left open rather
                    // than holding the writer lock across the rest of the walk.
                    // A no-op when no transaction is open, which in the steady
                    // state is every file.
                    //
                    // BatchSize still does its job where it matters: during a
                    // bulk convergence pass consecutive files all write, so
                    // they batch, and only a file that needs nothing breaks the
                    // run.
                    ctx.Flush();
                }
                return IngestOutcome.Unchanged;
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
            // Record the identity observed BEFORE the parse, not a fresh stat
            // here. If the file changed while we were reading it, the stale
            // recorded identity makes the next scan re-parse (safe, one wasted
            // parse); re-stat-ing now would record the new file's identity
            // against the old file's content and skip it forever.
            syncState.Upsert(
                ctx.Connection, ctx.Transaction, filePath, parsed.MessageId, indexedAt, parsed.ContentHash, folderName,
                fileMtimeUtc: mtimeUtc, fileSize: sizeBytes);
            ctx.NoteWrite();
            return IngestOutcome.Upserted;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse {Path}", filePath);
            // PRESERVE the prior message_id. Nulling it would drop this path
            // out of the deletion-reconciliation mapping (it filters on
            // message_id != null), so if the file is later removed, its
            // message would be stranded "live" forever.
            //
            // This write is no longer what keeps the file from being treated
            // as deleted — the walk already recorded the path in
            // observedPaths. It is here to leave the retry marker below.
            //
            // content_hash is deliberately NULLed as a "retry me" marker: the
            // mtime fast path requires a non-null hash, so the next scan
            // re-parses this file instead of trusting the fresh last_seen_at
            // stamp. Preserving the prior hash here would let a transient
            // failure on a changed file silently mask the change forever.
            try
            {
                // File identity is NULLed alongside content_hash, for the
                // same reason: this row is a "retry me" marker, and recording
                // an identity we never successfully ingested would let the
                // next scan treat the failure as a completed ingest.
                syncState.Upsert(
                    ctx.Connection, ctx.Transaction, filePath, prior?.MessageId, indexedAt, contentHash: null, folderName,
                    fileMtimeUtc: null, fileSize: null);
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

    /// <summary>
    /// Is the file on disk the one we recorded at the last ingest?
    /// </summary>
    /// <remarks>
    /// Equality on the observed mtime + size. Both are written together by the
    /// same <c>Upsert</c>, so a row has either both or neither.
    ///
    /// <para>A NULL identity means the row predates migration 009 (or was
    /// written by a path that couldn't observe one). Those fall back to the
    /// old <c>mtime &lt;= last_seen_at</c> comparison for exactly one scan —
    /// the caller records the real identity on the same pass, so every live
    /// row converges after one full scan. The alternative, treating NULL as a
    /// miss, would re-parse the entire corpus on first run after upgrade
    /// (every PDF and DOCX re-extracted) to close a window that only a restore
    /// tool can open. Not worth it; the fallback is what the code did anyway
    /// until the row is refreshed.</para>
    ///
    /// <para><b>Residual gap, known and accepted:</b> a replacement that keeps
    /// BOTH the mtime and the byte count is still skipped. Only hashing the
    /// file would catch that, and hashing every file on every scan is the
    /// exact cost this fast path exists to avoid (it dominates indexer CPU on
    /// a real corpus). mtime+size closes the realistic restore case — rsync/cp
    /// /tar preserve mtime, but a restored file whose content actually changed
    /// almost never lands on the same byte count. If that ever stops being
    /// good enough, the next step is a cheap content signature, NOT relaxing
    /// this back to a comparison against scan time.</para>
    /// </remarks>
    private static bool FileIdentityUnchanged(SyncStateEntry prior, DateTime mtimeUtc, long sizeBytes)
    {
        if (prior.FileMtimeUtc is not { } recordedMtime)
            return mtimeUtc <= prior.LastSeenAt.UtcDateTime;

        return recordedMtime.UtcDateTime == mtimeUtc
            && prior.FileSize == sizeBytes;
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
///
/// Two consequences of unchanged files no longer writing, both load-bearing:
/// TryIngest reads through <see cref="CurrentTransaction"/> rather than
/// <see cref="Transaction"/>, and it calls <see cref="Flush"/> on any file
/// that needs no write. NoteWrite no longer fires on every file, so
/// <c>_batchSize</c> alone no longer bounds how long a transaction stays open;
/// without both of those an idle scan holds the writer lock across the entire
/// walk while writing nothing at all.
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

    /// <summary>
    /// The rolling transaction, BEGINNING one if none is open. Write paths
    /// only — see <see cref="CurrentTransaction"/>.
    /// </summary>
    public SqliteTransaction Transaction
    {
        get
        {
            if (_tx is null)
            {
                _tx = _connection.BeginTransaction();
                TransactionsBegun++;
            }
            return _tx;
        }
    }

    /// <summary>
    /// How many transactions this context has BEGUN. See
    /// <c>MaildirScanner.LastScanTransactionsBegun</c>.
    /// </summary>
    public int TransactionsBegun { get; private set; }

    /// <summary>
    /// The transaction currently open, or null. Never begins one.
    /// </summary>
    /// <remarks>
    /// Reads must use this. <see cref="Transaction"/> begins BEGIN IMMEDIATE on
    /// first touch, so a read that reaches for it takes the writer lock — and
    /// the scanner's per-file <c>sync_state</c> lookup would then hold that
    /// lock for the whole walk, writing nothing the entire time.
    /// </remarks>
    public SqliteTransaction? CurrentTransaction => _tx;

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
