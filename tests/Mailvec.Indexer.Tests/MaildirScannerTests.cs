using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Indexer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailvec.Indexer.Tests;

public class MaildirScannerTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly ConnectionFactory _connections;
    private readonly MessageRepository _messages;
    private readonly ChunkRepository _chunks;
    private readonly SyncStateRepository _syncState;
    private readonly MaildirScanner _scanner;

    public MaildirScannerTests()
    {
        var temp = Path.Combine(Path.GetTempPath(), "mailvec-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        _root = Path.Combine(temp, "Mail");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(temp, "archive.sqlite");

        var archiveOptions = Microsoft.Extensions.Options.Options.Create(new ArchiveOptions
        {
            DatabasePath = _dbPath,
        });
        var ingestOptions = Microsoft.Extensions.Options.Options.Create(new IngestOptions
        {
            MaildirRoot = _root,
        });

        _connections = new ConnectionFactory(archiveOptions);
        new SchemaMigrator(_connections, NullLogger<SchemaMigrator>.Instance).EnsureUpToDate();

        _messages = new MessageRepository(_connections);
        _chunks = new ChunkRepository(_connections);
        _syncState = new SyncStateRepository(_connections);
        _scanner = new MaildirScanner(
            ingestOptions,
            new MessageParser(),
            _messages,
            _chunks,
            _syncState,
            _connections,
            NullLogger<MaildirScanner>.Instance);
    }

    public void Dispose()
    {
        // Scope the pool clear to THIS database (see TempDatabase) — a global
        // ClearAllPools() races with parallel test classes' in-use connections.
        using (var conn = _connections.Open())
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(conn);
        }
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private string WriteEml(string folder, string subdir, string filename, string body, string messageId)
    {
        var dir = Path.Combine(_root, folder, subdir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, filename);
        File.WriteAllText(path, $"""
            Message-ID: <{messageId}>
            Date: Mon, 13 Jan 2025 10:15:00 -0500
            From: alice@example.com
            To: bob@example.com
            Subject: Test
            MIME-Version: 1.0
            Content-Type: text/plain; charset=utf-8

            {body}

            """);
        return path;
    }

    [Fact]
    public void Scans_a_fresh_maildir_and_inserts_messages()
    {
        WriteEml("INBOX", "cur", "1.host:2,S", "hello world",                "msg-001@x");
        WriteEml("INBOX", "new", "2.host",     "another one",                "msg-002@x");
        WriteEml("Archive.2024", "cur", "3.host:2,S", "older message text",  "msg-003@x");

        var result = _scanner.ScanAll();

        result.Seen.ShouldBe(3);
        result.Upserted.ShouldBe(3);
        result.FailedToParse.ShouldBe(0);
        result.SoftDeleted.ShouldBe(0);
        _messages.CountAll().ShouldBe(3);

        var inbox = _messages.GetByMessageId("msg-001@x").ShouldNotBeNull();
        inbox.Folder.ShouldBe("INBOX");
        inbox.MaildirPath.ShouldBe("INBOX/cur");

        var archived = _messages.GetByMessageId("msg-003@x").ShouldNotBeNull();
        archived.Folder.ShouldBe("Archive.2024");
    }

    [Fact]
    public void Re_scanning_after_a_file_is_deleted_soft_deletes_the_message()
    {
        var keep = WriteEml("INBOX", "cur", "k.host:2,S", "keep this",   "keep@x");
        var drop = WriteEml("INBOX", "cur", "d.host:2,S", "drop this",   "drop@x");

        _scanner.ScanAll();
        _messages.CountAll().ShouldBe(2);

        File.Delete(drop);
        var second = _scanner.ScanAll();

        second.SoftDeleted.ShouldBe(1);
        _messages.CountAll().ShouldBe(1);

        var keptMsg = _messages.GetByMessageId("keep@x").ShouldNotBeNull();
        keptMsg.DeletedAt.ShouldBeNull();
        var droppedMsg = _messages.GetByMessageId("drop@x").ShouldNotBeNull();
        droppedMsg.DeletedAt.ShouldNotBeNull();
        File.Exists(keep).ShouldBeTrue();
    }

    [Fact]
    public void Body_change_clears_embeddings_on_rescan()
    {
        // Initial scan + simulated embedding.
        var path = WriteEml("INBOX", "cur", "edit.host:2,S", "original body", "edit@x");
        _scanner.ScanAll();
        var msg = _messages.GetByMessageId("edit@x").ShouldNotBeNull();

        _chunks.ReplaceChunksForMessage(
            msg.Id,
            [new Mailvec.Core.Embedding.TextChunk(0, "chunk text", 1)],
            [Hot(0)],
            DateTimeOffset.UtcNow);
        _chunks.CountForMessage(msg.Id).ShouldBe(1);
        EmbeddedAt(msg.Id).ShouldNotBeNull();

        // Rewrite the .eml with a different body but the same Message-ID.
        File.Delete(path);
        WriteEml("INBOX", "cur", "edit.host:2,S", "completely different body", "edit@x");

        _scanner.ScanAll();

        // Embeddings should be cleared.
        _chunks.CountForMessage(msg.Id).ShouldBe(0);
        EmbeddedAt(msg.Id).ShouldBeNull();
    }

    [Fact]
    public void Header_only_change_does_not_clear_embeddings()
    {
        var path = WriteEml("INBOX", "cur", "headers.host:2,S", "stable body", "headers@x");
        _scanner.ScanAll();
        var msg = _messages.GetByMessageId("headers@x").ShouldNotBeNull();

        _chunks.ReplaceChunksForMessage(
            msg.Id,
            [new Mailvec.Core.Embedding.TextChunk(0, "chunk text", 1)],
            [Hot(0)],
            DateTimeOffset.UtcNow);

        // Rewrite with extra headers but identical body.
        File.Delete(path);
        var dir = Path.GetDirectoryName(path)!;
        File.WriteAllText(path, $"""
            Message-ID: <headers@x>
            Date: Mon, 13 Jan 2025 10:15:00 -0500
            From: alice@example.com
            To: bob@example.com
            Subject: Test
            X-Spam-Score: 0.0
            DKIM-Verified: pass
            MIME-Version: 1.0
            Content-Type: text/plain; charset=utf-8

            stable body

            """);

        _scanner.ScanAll();

        // Body unchanged -> embeddings preserved.
        _chunks.CountForMessage(msg.Id).ShouldBe(1);
        EmbeddedAt(msg.Id).ShouldNotBeNull();
    }

    [Fact]
    public void Duplicate_copies_across_folders_are_searchable_under_each_folder()
    {
        // Gmail-shaped corpus: the same Message-ID lives in All Mail AND a
        // label folder. One messages row (never one per copy), but folder
        // filtering and list_folders must see every membership.
        WriteEml("INBOX",   "cur", "d1.host:2,S", "quarterly report attached", "dup@x");
        WriteEml("AllMail", "cur", "d2.host:2,S", "quarterly report attached", "dup@x");

        _scanner.ScanAll();
        _messages.CountAll().ShouldBe(1);

        var keyword = new Mailvec.Core.Search.KeywordSearchService(_connections);
        keyword.Search("quarterly", 10, new Mailvec.Core.Search.SearchFilters(Folder: "INBOX")).Count.ShouldBe(1);
        keyword.Search("quarterly", 10, new Mailvec.Core.Search.SearchFilters(Folder: "AllMail")).Count.ShouldBe(1);
        keyword.Search("quarterly", 10, new Mailvec.Core.Search.SearchFilters(Folder: "Elsewhere")).Count.ShouldBe(0);

        var stats = _messages.FolderStats();
        stats.Single(s => s.Folder == "INBOX").MessageCount.ShouldBe(1);
        stats.Single(s => s.Folder == "AllMail").MessageCount.ShouldBe(1);
    }

    [Fact]
    public void Folder_attribution_is_stable_when_the_other_copy_is_rewritten()
    {
        WriteEml("INBOX",   "cur", "s1.host:2,S", "same body", "sticky@x");
        WriteEml("AllMail", "cur", "s2.host:2,S", "same body", "sticky@x");
        _scanner.ScanAll();

        var winner = _messages.GetByMessageId("sticky@x").ShouldNotBeNull().Folder;
        var loserFolder = winner == "INBOX" ? "AllMail" : "INBOX";
        var loserFile = winner == "INBOX" ? "s2.host:2,S" : "s1.host:2,S";

        // Rewrite the non-attributed copy: mtime bumps → full reparse →
        // upsert conflict. Under the old last-writer-wins clause this flipped
        // the attributed folder to whichever copy was parsed most recently.
        File.Delete(Path.Combine(_root, loserFolder, "cur", loserFile));
        WriteEml(loserFolder, "cur", loserFile, "same body", "sticky@x");
        _scanner.ScanAll();

        _messages.GetByMessageId("sticky@x").ShouldNotBeNull().Folder.ShouldBe(winner);
    }

    [Fact]
    public void Deleting_the_attributed_copy_repoints_to_the_surviving_copy()
    {
        WriteEml("INBOX",   "cur", "r1.host:2,S", "same body", "repoint@x");
        WriteEml("AllMail", "cur", "r2.host:2,S", "same body", "repoint@x");
        _scanner.ScanAll();

        var msg = _messages.GetByMessageId("repoint@x").ShouldNotBeNull();
        var survivor = msg.Folder == "INBOX" ? "AllMail" : "INBOX";
        File.Delete(Path.Combine(_root, msg.Folder, "cur", msg.MaildirFilename));

        var second = _scanner.ScanAll();

        // Not a deletion: the message lives on via the other copy, and the
        // rename-repair pass (now load-bearing, since the upsert no longer
        // rewrites the location triple) repoints attribution to it.
        second.SoftDeleted.ShouldBe(0);
        var after = _messages.GetByMessageId("repoint@x").ShouldNotBeNull();
        after.DeletedAt.ShouldBeNull();
        after.Folder.ShouldBe(survivor);
    }

    [Fact]
    public void Resurrected_message_takes_the_new_copys_folder()
    {
        // The keeper exists so the deletion scan still sees >0 files — a scan
        // that sees zero files skips reconciliation entirely (the empty-root
        // guard), which would keep lazarus alive and never soft-delete it.
        WriteEml("INBOX", "cur", "keeper.host:2,S", "stays alive", "keeper@x");
        var path = WriteEml("INBOX", "cur", "z.host:2,S", "back from the dead", "lazarus@x");
        _scanner.ScanAll();
        File.Delete(path);
        _scanner.ScanAll();
        _messages.GetByMessageId("lazarus@x").ShouldNotBeNull().DeletedAt.ShouldNotBeNull();

        // Same Message-ID reappears in a different folder (restored from
        // Trash, re-delivered). The stored INBOX path is dead and its
        // sync_state row is gone, so no repair pass will ever fix it — the
        // conflict clause must take the new copy's location on resurrection.
        WriteEml("Restored", "cur", "z2.host:2,S", "back from the dead", "lazarus@x");
        _scanner.ScanAll();

        var msg = _messages.GetByMessageId("lazarus@x").ShouldNotBeNull();
        msg.DeletedAt.ShouldBeNull();
        msg.Folder.ShouldBe("Restored");
        msg.MaildirFilename.ShouldBe("z2.host:2,S");
    }

    [Fact]
    public void Empty_maildir_root_does_not_mass_soft_delete_the_archive()
    {
        WriteEml("INBOX", "cur", "a.host:2,S", "message one", "one@x");
        WriteEml("INBOX", "cur", "b.host:2,S", "message two", "two@x");
        _scanner.ScanAll();
        _messages.CountAll().ShouldBe(2);

        // The root suddenly presents as empty — a network mount racing up,
        // a re-pointed MaildirRoot, or mbsync mid-re-init. Pre-guard, every
        // sync_state row went stale and the WHOLE archive was soft-deleted
        // in one scan (and purge-deleted in that window would have made it
        // permanent).
        Directory.Delete(Path.Combine(_root, "INBOX"), recursive: true);

        var result = _scanner.ScanAll();

        result.SoftDeleted.ShouldBe(0);
        _messages.CountAll().ShouldBe(2);   // still live

        // Once ANY file is visible again, reconciliation resumes — a
        // genuinely emptied mailbox isn't shielded forever.
        WriteEml("INBOX", "cur", "a.host:2,S", "message one", "one@x");
        var healed = _scanner.ScanAll();
        healed.SoftDeleted.ShouldBe(1);     // two@x really is gone
        _messages.GetByMessageId("one@x").ShouldNotBeNull().DeletedAt.ShouldBeNull();
        _messages.GetByMessageId("two@x").ShouldNotBeNull().DeletedAt.ShouldNotBeNull();
    }

    [Fact]
    public void Unreadable_folder_is_skipped_and_deletion_reconciliation_deferred()
    {
        // chmod can't block root (some CI containers), and unix modes don't
        // exist on Windows — in both cases the scenario is untestable.
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;

        WriteEml("INBOX", "cur", "ok.host:2,S", "readable message", "ok@x");
        WriteEml("Blocked", "cur", "b.host:2,S", "behind a locked door", "blocked@x");
        _scanner.ScanAll();
        _messages.CountAll().ShouldBe(2);

        var blockedDir = Path.Combine(_root, "Blocked");
        var original = File.GetUnixFileMode(blockedDir);
        File.SetUnixFileMode(blockedDir, UnixFileMode.None);
        try
        {
            // One unreadable directory must not abort the walk (pre-fix this
            // threw UnauthorizedAccessException out of ScanAll, which under
            // launchd KeepAlive is a startup crash loop)...
            var result = Should.NotThrow(() => _scanner.ScanAll());

            // ...and the message whose file is alive-but-unlistable must not
            // be soft-deleted: reconciliation is skipped for this scan.
            result.SoftDeleted.ShouldBe(0);
            _messages.GetByMessageId("blocked@x").ShouldNotBeNull().DeletedAt.ShouldBeNull();
            // The readable folder was still scanned.
            result.Seen.ShouldBe(1);
        }
        finally
        {
            File.SetUnixFileMode(blockedDir, original);
        }

        // Readable again: a normal scan sees both files and deletes nothing.
        var healed = _scanner.ScanAll();
        healed.Seen.ShouldBe(2);
        healed.SoftDeleted.ShouldBe(0);
    }

    [Fact]
    public void Unreadable_cur_subdir_is_skipped_and_deletion_reconciliation_deferred()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;

        WriteEml("INBOX", "cur", "ok.host:2,S", "readable message", "ok@x");
        WriteEml("Locked", "cur", "l.host:2,S", "cur is locked", "locked@x");
        _scanner.ScanAll();

        // The folder dir stays listable, only its cur/ is not — exercises the
        // per-subdir GetFiles guard rather than the walker's GetDirectories one.
        var lockedCur = Path.Combine(_root, "Locked", "cur");
        var original = File.GetUnixFileMode(lockedCur);
        File.SetUnixFileMode(lockedCur, UnixFileMode.None);
        try
        {
            var result = Should.NotThrow(() => _scanner.ScanAll());

            result.SoftDeleted.ShouldBe(0);
            _messages.GetByMessageId("locked@x").ShouldNotBeNull().DeletedAt.ShouldBeNull();
            result.Seen.ShouldBe(1);
        }
        finally
        {
            File.SetUnixFileMode(lockedCur, original);
        }
    }

    private string? EmbeddedAt(long messageId)
    {
        using var conn = _connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT embedded_at FROM messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", messageId);
        var raw = cmd.ExecuteScalar();
        return raw is string s ? s : null;
    }

    private static float[] Hot(int idx, int dim = 1024)
    {
        var v = new float[dim];
        v[idx] = 1f;
        return v;
    }

    [Fact]
    public void Rescan_skips_unchanged_files_via_mtime_fast_path()
    {
        // After an initial scan, files whose mtime hasn't changed should not
        // be re-parsed on the next scan. The fast path guards against
        // re-running attachment text extraction (PdfPig / OpenXml) every
        // 5 minutes against the entire archive — once the corpus is large,
        // re-parsing every file is the dominant cost. We verify by mutating
        // body_text directly in the DB after the first scan; if the second
        // scan re-parsed the file it would overwrite our edit, so the
        // edit surviving proves the parse was skipped.
        WriteEml("INBOX", "cur", "stable.host:2,S", "original body", "stable@x");
        _scanner.ScanAll();
        var msg = _messages.GetByMessageId("stable@x").ShouldNotBeNull();

        using (var conn = _connections.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE messages SET body_text = 'sentinel' WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", msg.Id);
            cmd.ExecuteNonQuery();
        }

        // Same file, same mtime -> fast path -> no re-parse.
        _scanner.ScanAll();

        var afterRescan = _messages.GetById(msg.Id).ShouldNotBeNull();
        afterRescan.BodyText.ShouldBe("sentinel");
    }

    [Fact]
    public void Rescan_reparses_a_file_whose_content_changed_under_a_restored_mtime()
    {
        // The fast path used to compare the file's mtime against
        // sync_state.last_seen_at — the time Mailvec LOOKED, not the metadata
        // it saw. Any file whose bytes changed while its mtime stayed at or
        // below the previous scan's start satisfied that comparison, and since
        // each scan re-stamped last_seen_at to a later value without parsing,
        // the skip perpetuated itself: body text, attachments, FTS and vectors
        // pinned forever to content no longer on disk, with no error.
        //
        // mbsync can't produce this (new files carry current times; flag
        // changes are renames, which land at a fresh path and miss the
        // path-keyed lookup). Restores do it by design — rsync -a, cp -p and
        // tar -x all preserve mtime — which is exactly when the archive most
        // needs to notice that bytes moved.
        //
        // The fix compares the RECORDED mtime + size for equality, so a
        // restored timestamp no longer implies unchanged content.
        var path = WriteEml("INBOX", "cur", "restored.host:2,S", "original body", "restored@x");
        _scanner.ScanAll();
        var msg = _messages.GetByMessageId("restored@x").ShouldNotBeNull();
        msg.BodyText.ShouldNotBeNull().ShouldContain("original body");

        // Second scan populates the v9 identity columns for rows written
        // before them (the legacy-fallback branch), so the equality check is
        // actually armed for the third scan below.
        _scanner.ScanAll();
        var mtimeBefore = File.GetLastWriteTimeUtc(path);

        // Replace the content and restore the original mtime, exactly as a
        // backup restore would. The replacement is deliberately a different
        // LENGTH: mtime+size cannot distinguish an equal-size replacement at
        // an identical mtime, and this test must not imply otherwise. See the
        // residual-gap note on FileIdentityUnchanged.
        WriteEml("INBOX", "cur", "restored.host:2,S", "a completely different and much longer replacement body", "restored@x");
        File.SetLastWriteTimeUtc(path, mtimeBefore);
        File.GetLastWriteTimeUtc(path).ShouldBe(mtimeBefore, "the test must actually restore the mtime, or it proves nothing");

        _scanner.ScanAll();

        _messages.GetById(msg.Id).ShouldNotBeNull().BodyText.ShouldNotBeNull()
            .ShouldContain("much longer replacement body");
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Transient_ingest_failure_on_a_changed_file_does_not_mask_the_change()
    {
        // A file's body changes, but the scan that should pick it up fails
        // transiently (I/O blip, SQLITE_BUSY past the timeout, ...). The
        // catch path stamps last_seen_at to the scan start — which is LATER
        // than the file's mtime — so without the NULL-content_hash retry
        // marker the mtime fast path would skip the file on every future
        // scan and the change would be silently masked forever.
        var path = WriteEml("INBOX", "cur", "flaky.host:2,S", "original body", "flaky@x");
        _scanner.ScanAll();
        _messages.GetByMessageId("flaky@x").ShouldNotBeNull().BodyText.ShouldNotBeNull().ShouldContain("original body");

        // Body changes on disk...
        WriteEml("INBOX", "cur", "flaky.host:2,S", "updated body", "flaky@x");

        // ...but the next scan can't read the file (simulated transient failure).
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var failing = _scanner.ScanAll();
            failing.FailedToParse.ShouldBe(1);
            failing.SoftDeleted.ShouldBe(0);
        }
        finally
        {
            File.SetUnixFileMode(path, mode);
        }

        // The message survived the failed scan and the NEXT scan retries the
        // parse (instead of trusting the fresh last_seen_at stamp) and picks
        // up the changed body.
        _messages.GetByMessageId("flaky@x").ShouldNotBeNull().DeletedAt.ShouldBeNull();

        var retry = _scanner.ScanAll();
        retry.FailedToParse.ShouldBe(0);
        retry.Upserted.ShouldBe(1);
        _messages.GetByMessageId("flaky@x").ShouldNotBeNull().BodyText.ShouldNotBeNull().ShouldContain("updated body");
    }

    [Fact]
    public void Reconciliation_is_skipped_when_a_live_files_sync_refresh_fails()
    {
        var path = WriteEml("INBOX", "cur", "wedge.host:2,S", "wedge body", "wedge@x");
        _scanner.ScanAll();
        _messages.GetByMessageId("wedge@x").ShouldNotBeNull().DeletedAt.ShouldBeNull();

        // Inject a persistent write failure for this file's sync_state row:
        // both the ingest attempt AND the catch handler's refresh now fail,
        // mimicking sustained SQLITE_BUSY / I/O trouble.
        Exec($"CREATE TRIGGER wedge_guard BEFORE UPDATE ON sync_state WHEN new.maildir_full_path = '{path}' BEGIN SELECT RAISE(ABORT, 'injected failure'); END");

        // The file must actually CHANGE for the scan to attempt a sync_state
        // write at all — an unchanged file takes the fast path, which writes
        // nothing and so cannot fail. (Before that write was removed, the
        // trigger fired on the fast path's own refresh and this rewrite wasn't
        // needed. The guard under test is unchanged; only the way to provoke
        // it is.)
        WriteEml("INBOX", "cur", "wedge.host:2,S", "wedge body, rewritten and longer", "wedge@x");

        var failing = _scanner.ScanAll();
        failing.FailedToParse.ShouldBe(1);
        failing.SoftDeleted.ShouldBe(0);
        _messages.GetByMessageId("wedge@x").ShouldNotBeNull().DeletedAt.ShouldBeNull();

        // Failure clears -> subsequent scans are back to normal.
        Exec("DROP TRIGGER wedge_guard");
        var recovered = _scanner.ScanAll();
        recovered.FailedToParse.ShouldBe(0);
        recovered.SoftDeleted.ShouldBe(0);
        _messages.GetByMessageId("wedge@x").ShouldNotBeNull().DeletedAt.ShouldBeNull();
    }

    // ---------------------------------------------------------------------
    // Steady-state write volume. An idle indexer used to rewrite every
    // sync_state row on every scan just to restamp last_seen_at — 82K rows,
    // one SQLite page each, once a minute, measured at 467 GB/day into the WAL
    // on the author's deployment. These pin the two halves of the fix: the
    // scan writes nothing, and it still notices everything.
    // ---------------------------------------------------------------------

    [Fact]
    public void An_unchanged_rescan_writes_nothing()
    {
        for (var i = 0; i < 40; i++)
            WriteEml("INBOX", "cur", $"{i}.host:2,S", $"body {i}", $"msg-{i}@x");

        var first = _scanner.ScanAll();
        first.Upserted.ShouldBe(40);
        first.Unchanged.ShouldBe(0);

        CheckpointWal();
        var walPath = _dbPath + "-wal";
        new FileInfo(walPath).Length.ShouldBe(0);
        var mainDbWrittenAt = File.GetLastWriteTimeUtc(_dbPath);

        var second = _scanner.ScanAll();

        // The acceptance criterion from the bug report: seen stays at the full
        // corpus count, upserted collapses to zero.
        second.Seen.ShouldBe(40);
        second.Upserted.ShouldBe(0);
        second.Unchanged.ShouldBe(40);
        second.FailedToParse.ShouldBe(0);
        second.SoftDeleted.ShouldBe(0);

        // And the bytes, which is what the report was actually about. Asserted
        // as an exact zero rather than a budget: any per-file write that
        // creeps back in is multiplied by the corpus size and by 1440 scans a
        // day, so "small" is not a safe threshold here.
        new FileInfo(walPath).Length.ShouldBe(0);
        File.GetLastWriteTimeUtc(_dbPath).ShouldBe(mainDbWrittenAt);
    }

    [Fact]
    public void Real_mail_still_lands_on_an_otherwise_unchanged_scan()
    {
        for (var i = 0; i < 10; i++)
            WriteEml("INBOX", "cur", $"{i}.host:2,S", $"body {i}", $"msg-{i}@x");
        _scanner.ScanAll();

        WriteEml("INBOX", "new", "fresh.host", "just arrived", "fresh@x");

        var result = _scanner.ScanAll();
        result.Seen.ShouldBe(11);
        // Only the real delta — not the whole corpus, and not zero.
        result.Upserted.ShouldBe(1);
        result.Unchanged.ShouldBe(10);
        _messages.GetByMessageId("fresh@x").ShouldNotBeNull();
    }

    [Fact]
    public void Deletion_is_still_detected_after_many_unchanged_scans()
    {
        var path = WriteEml("INBOX", "cur", "doomed.host:2,S", "doomed body", "doomed@x");
        WriteEml("INBOX", "cur", "keeper.host:2,S", "keeper body", "keeper@x");
        _scanner.ScanAll();

        // Each of these leaves last_seen_at untouched, so by the end the rows
        // look ancient. Deletion detection must not care: it is now "tracked
        // but not walked", not "tracked and stale".
        for (var i = 0; i < 5; i++) _scanner.ScanAll();

        File.Delete(path);
        var result = _scanner.ScanAll();

        result.SoftDeleted.ShouldBe(1);
        _messages.GetByMessageId("doomed@x").ShouldNotBeNull().DeletedAt.ShouldNotBeNull();
        _messages.GetByMessageId("keeper@x").ShouldNotBeNull().DeletedAt.ShouldBeNull();
    }

    [Fact]
    public void A_surviving_copy_is_still_recognised_after_many_unchanged_scans()
    {
        // The same Message-ID in two folders (Fastmail labels). Both rows are
        // written by the first scan and never restamped again.
        var inboxCopy = WriteEml("INBOX", "cur", "dup.host:2,S", "dup body", "dup@x");
        WriteEml("Archive.2024", "cur", "dup.host:2,S", "dup body", "dup@x");
        _scanner.ScanAll();

        for (var i = 0; i < 5; i++) _scanner.ScanAll();

        // Delete one copy. The message survives at the other path — which the
        // scan must establish WITHOUT asking whether the survivor's row was
        // stamped recently, because it wasn't. Reading survivorship out of
        // last_seen_at here would soft-delete live mail.
        File.Delete(inboxCopy);
        var result = _scanner.ScanAll();

        result.SoftDeleted.ShouldBe(0);
        var msg = _messages.GetByMessageId("dup@x").ShouldNotBeNull();
        msg.DeletedAt.ShouldBeNull();
        // ...and the row is repointed at the copy that still exists.
        File.Exists(Path.Combine(_root, msg.MaildirPath, msg.MaildirFilename)).ShouldBeTrue();
    }

    [Fact]
    public void An_mbsync_rename_after_many_unchanged_scans_is_not_a_deletion()
    {
        var newPath = WriteEml("INBOX", "new", "renamed.host", "renamed body", "renamed@x");
        _scanner.ScanAll();
        for (var i = 0; i < 5; i++) _scanner.ScanAll();

        var curPath = Path.Combine(_root, "INBOX", "cur", "renamed.host:2,S");
        Directory.CreateDirectory(Path.GetDirectoryName(curPath)!);
        File.Move(newPath, curPath);

        var result = _scanner.ScanAll();

        result.SoftDeleted.ShouldBe(0);
        _messages.CountAll().ShouldBe(1);
        var msg = _messages.GetByMessageId("renamed@x").ShouldNotBeNull();
        msg.DeletedAt.ShouldBeNull();
        msg.MaildirPath.ShouldBe("INBOX/cur");
    }

    [Fact]
    public void A_row_missing_its_file_identity_records_one_and_then_goes_quiet()
    {
        var path = WriteEml("INBOX", "cur", "legacy.host:2,S", "legacy body", "legacy@x");
        _scanner.ScanAll();

        // Simulate a row written before migration 009. The fast path matches
        // such a row through the legacy `mtime <= last_seen_at` fallback — the
        // very hole 009 closed — so it MUST still take the one write that
        // records the real identity. With last_seen_at also frozen now, a row
        // left on that fallback would never converge.
        Exec($"UPDATE sync_state SET file_mtime_utc = NULL, file_size = NULL WHERE maildir_full_path = '{path}'");

        _scanner.ScanAll().Unchanged.ShouldBe(1);
        Scalar($"SELECT file_mtime_utc FROM sync_state WHERE maildir_full_path = '{path}'").ShouldNotBeNull();
        Scalar($"SELECT file_size FROM sync_state WHERE maildir_full_path = '{path}'").ShouldNotBeNull();

        // Convergence is one-shot: the next scan is back to writing nothing.
        CheckpointWal();
        _scanner.ScanAll();
        new FileInfo(_dbPath + "-wal").Length.ShouldBe(0);
    }

    [Fact]
    public void A_row_missing_its_folder_records_one_and_then_goes_quiet()
    {
        var path = WriteEml("Archive.2024", "cur", "nofolder.host:2,S", "body", "nofolder@x");
        _scanner.ScanAll();

        // sync_state IS the folder-membership table that search's EXISTS probe
        // and FolderStats read, so a pre-008 NULL here silently shrinks what
        // folder filters match. The skip-the-write fast path must not leave it
        // that way.
        Exec($"UPDATE sync_state SET folder = NULL WHERE maildir_full_path = '{path}'");

        _scanner.ScanAll().Unchanged.ShouldBe(1);
        Scalar($"SELECT folder FROM sync_state WHERE maildir_full_path = '{path}'").ShouldBe("Archive.2024");

        CheckpointWal();
        _scanner.ScanAll();
        new FileInfo(_dbPath + "-wal").Length.ShouldBe(0);
    }

    [Fact]
    public void An_unchanged_scan_leaves_the_writer_lock_free()
    {
        // Removing the fast path's write removes its ctx.NoteWrite() too, and
        // NoteWrite is what used to commit the rolling transaction every
        // BatchSize rows. The read at the top of TryIngest still asks
        // ScanContext for a transaction, and that property BEGINs one on first
        // touch — BEGIN IMMEDIATE, which takes SQLite's single writer slot. So
        // "writes nothing" is not the same as "blocks nobody": an idle scan
        // could hold the writer lock across the entire 82K-file walk, with the
        // embedder, the OCR write-back, every maintenance command and MCP's
        // startup migration queued behind it. Zero WAL bytes the whole time.
        //
        // The probe takes the writer lock from an independent connection while
        // the walk is in flight. It runs once, on the first file, with a short
        // busy timeout so a regression is fast and red rather than a 30-second
        // stall.
        for (var i = 0; i < 20; i++)
            WriteEml("INBOX", "cur", $"{i}.host:2,S", $"body {i}", $"msg-{i}@x");
        _scanner.ScanAll();

        var probed = false;
        var writerLockWasFree = true;
        _scanner.OnFileWalked = () =>
        {
            if (probed) return;
            probed = true;
            writerLockWasFree = TryTakeWriterLock();
        };

        try
        {
            var result = _scanner.ScanAll();
            result.Unchanged.ShouldBe(20);
            result.Upserted.ShouldBe(0);
        }
        finally
        {
            _scanner.OnFileWalked = null;
        }

        probed.ShouldBeTrue("the probe must actually have run, or this proves nothing");
        writerLockWasFree.ShouldBeTrue(
            "an unchanged scan held SQLite's writer lock across the walk, blocking every other writer");
    }

    [Fact]
    public void An_unchanged_scan_never_takes_the_writer_lock_at_all()
    {
        // The companion to the probe test, and strictly sharper. Flushing
        // promptly bounds how LONG the lock is held; it does not stop the scan
        // taking it. If the per-file sync_state read asks ScanContext for a
        // transaction rather than for the one already open, every unchanged
        // file BEGINs IMMEDIATE and commits again — 82K writer-lock
        // acquisitions per scan on a real corpus — and nothing else here can
        // see it: the probe between files finds the lock free, and an empty
        // transaction dirties no pages so the WAL stays at zero.
        for (var i = 0; i < 20; i++)
            WriteEml("INBOX", "cur", $"{i}.host:2,S", $"body {i}", $"msg-{i}@x");

        _scanner.ScanAll();
        _scanner.LastScanTransactionsBegun.ShouldBeGreaterThan(0, "the first scan writes, so it must open transactions");

        var second = _scanner.ScanAll();
        second.Unchanged.ShouldBe(20);
        _scanner.LastScanTransactionsBegun.ShouldBe(0);
    }

    [Fact]
    public void A_scan_that_writes_releases_the_writer_lock_once_the_writes_stop()
    {
        // Same hazard from the other side. A scan that ingests one new message
        // opens the rolling transaction for its sync_state write; if nothing
        // ever commits it, the lock is held from that file to the end of the
        // walk. Under the old code the following files' own fast-path writes
        // reached BatchSize and flushed it; they no longer write at all.
        for (var i = 0; i < 20; i++)
            WriteEml("INBOX", "cur", $"{i}.host:2,S", $"body {i}", $"msg-{i}@x");
        _scanner.ScanAll();

        // Sorts first in the directory listing, so the write happens early in
        // the walk and most of the walk follows it.
        WriteEml("INBOX", "cur", "0000-new.host", "just arrived", "arrived@x");

        var probes = 0;
        var writerLockWasFree = true;
        _scanner.OnFileWalked = () =>
        {
            // Probe on the LAST file, by which point the earlier write's
            // transaction must have been committed.
            if (++probes != 21) return;
            writerLockWasFree = TryTakeWriterLock();
        };

        try
        {
            var result = _scanner.ScanAll();
            result.Upserted.ShouldBe(1);
            result.Unchanged.ShouldBe(20);
        }
        finally
        {
            _scanner.OnFileWalked = null;
        }

        probes.ShouldBe(21);
        writerLockWasFree.ShouldBeTrue(
            "the rolling transaction outlived the writes it was batching and held the writer lock for the rest of the walk");
    }

    /// <summary>
    /// Try to take SQLite's writer lock from a connection of our own. False if
    /// something else is holding it.
    /// </summary>
    /// <remarks>
    /// A raw connection rather than the shared ConnectionFactory, purely so the
    /// timeouts can be short: the factory waits 30 s, which would turn a
    /// regression into a stalled test run rather than a quick red one. Both
    /// knobs are needed — the busy_timeout pragma sleeps inside a single
    /// statement step, while Default Timeout bounds Microsoft.Data.Sqlite's
    /// own retry loop around it, and that is the one that actually decides how
    /// long a blocked BEGIN IMMEDIATE waits.
    /// </remarks>
    private bool TryTakeWriterLock()
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Default Timeout=1");
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout = 200;";
                pragma.ExecuteScalar();
            }
            using var tx = conn.BeginTransaction(); // BEGIN IMMEDIATE
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE metadata SET value = value WHERE key = 'schema_version'";
            cmd.ExecuteNonQuery();
            tx.Commit();
            return true;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// Truncate the WAL so a following scan's writes are the only bytes in it.
    /// </summary>
    /// <remarks>
    /// <para>ExecuteScalar, not ExecuteNonQuery: <c>wal_checkpoint</c> returns a
    /// row, and Microsoft.Data.Sqlite silently no-ops result-returning pragmas
    /// under ExecuteNonQuery — the same trap that left the schema's
    /// <c>journal_mode = WAL</c> not actually applying. A silently skipped
    /// checkpoint here would leave the WAL non-empty and quietly defeat the
    /// measurement.</para>
    ///
    /// <para>TRUNCATE rather than PASSIVE, and the callers assert the resulting
    /// zero as a precondition rather than assuming it. SQLite reuses WAL frames
    /// in place after a checkpoint, so on a merely-checkpointed WAL a real write
    /// need not grow the file at all — measuring a delta against a non-zero
    /// baseline could read a full-corpus rewrite as zero bytes.</para>
    /// </remarks>
    private void CheckpointWal()
    {
        using var conn = _connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteScalar();
    }

    private object? Scalar(string sql)
    {
        using var conn = _connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private void Exec(string sql)
    {
        using var conn = _connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Deleting_the_referenced_duplicate_copy_repoints_maildir_path_to_the_survivor()
    {
        // Fastmail labels: the same Message-ID lives in two folders. The
        // messages row records whichever copy scanned last; if THAT copy is
        // deleted, rename-detection correctly keeps the message alive — but
        // the survivor rides the mtime fast-path and never re-upserts, so the
        // row's maildir_path used to dangle forever (view_attachment fails,
        // OCR skips its attachments every cycle).
        WriteEml("INBOX", "cur", "dup.host:2,S", "same body", "dup@x");
        WriteEml("Archive.2024", "cur", "dup.host:2,S", "same body", "dup@x");
        _scanner.ScanAll();
        _messages.CountAll().ShouldBe(1);

        var before = _messages.GetByMessageId("dup@x").ShouldNotBeNull();
        // Delete exactly the copy the row references.
        var referenced = Path.Combine(_root, before.MaildirPath, before.MaildirFilename);
        File.Exists(referenced).ShouldBeTrue();
        File.Delete(referenced);
        var survivorFolder = before.MaildirPath.StartsWith("INBOX", StringComparison.Ordinal) ? "Archive.2024" : "INBOX";

        var second = _scanner.ScanAll();

        second.SoftDeleted.ShouldBe(0);
        var after = _messages.GetByMessageId("dup@x").ShouldNotBeNull();
        after.DeletedAt.ShouldBeNull();
        after.Folder.ShouldBe(survivorFolder);
        after.MaildirPath.ShouldBe($"{survivorFolder}/cur");
        File.Exists(Path.Combine(_root, after.MaildirPath, after.MaildirFilename)).ShouldBeTrue();
    }

    // ── Divergent duplicate copies ───────────────────────────────────────────
    //
    // One Message-ID can legitimately live in several folders, and those copies
    // are not always byte-identical: a mailing-list copy carries an appended
    // footer the Sent copy doesn't. Folder attribution is first-seen-wins, so
    // without a matching rule for CONTENT the row ends up describing copy A's
    // location and copy B's bytes — and everything that resolves a part_index
    // against the attributed .eml (view_attachment, the OCR pass) then reads
    // the wrong document.

    /// <summary>Two copies of one Message-ID whose bodies genuinely differ.</summary>
    private void WriteDivergentPair(string messageId, string inboxMarker, string otherMarker)
    {
        WriteEml("INBOX", "cur", "dv1.host:2,S", $"shared body {inboxMarker}", messageId);
        WriteEml("Lists", "cur", "dv2.host:2,S", $"shared body {otherMarker}", messageId);
    }

    private string MarkerFor(Mailvec.Core.Models.Message msg) =>
        msg.Folder == "INBOX" ? "INBOXCOPY" : "LISTSCOPY";

    [Fact]
    public void Divergent_duplicate_copies_leave_the_row_aligned_with_the_attributed_file()
    {
        WriteDivergentPair("dv@x", "INBOXCOPY", "LISTSCOPY");

        _scanner.ScanAll();

        // Whichever copy won attribution, the stored body must be the body of
        // the file the row points at — otherwise a part_index resolved against
        // that file addresses a document the metadata never described.
        var msg = _messages.GetByMessageId("dv@x").ShouldNotBeNull();
        var onDisk = File.ReadAllText(Path.Combine(_root, msg.MaildirPath, msg.MaildirFilename));
        onDisk.ShouldContain(MarkerFor(msg));                 // sanity: the pair really did diverge
        msg.BodyText.ShouldNotBeNull().ShouldContain(MarkerFor(msg));
    }

    [Fact]
    public void Divergent_duplicate_copies_do_not_re_queue_the_message_on_every_rescan()
    {
        WriteDivergentPair("churn@x", "INBOXCOPY", "LISTSCOPY");
        _scanner.ScanAll();
        var msg = _messages.GetByMessageId("churn@x").ShouldNotBeNull();

        _chunks.ReplaceChunksForMessage(
            msg.Id,
            [new Mailvec.Core.Embedding.TextChunk(0, "chunk text", 1)],
            [Hot(0)],
            DateTimeOffset.UtcNow);
        EmbeddedAt(msg.Id).ShouldNotBeNull();

        // Rewrite BOTH copies with their same contents: mtimes bump, so the
        // fast path is skipped and both are re-parsed, but nothing actually
        // changed. With the row's content_hash alternating between the two
        // copies, every such rescan looks like a body change and burns a full
        // re-embed of the message — forever.
        WriteDivergentPair("churn@x", "INBOXCOPY", "LISTSCOPY");

        _scanner.ScanAll();

        EmbeddedAt(msg.Id).ShouldNotBeNull();
        _chunks.CountForMessage(msg.Id).ShouldBe(1);
    }

    [Fact]
    public void Repointing_to_a_divergent_survivor_realigns_the_stored_content()
    {
        WriteDivergentPair("repoint-dv@x", "INBOXCOPY", "LISTSCOPY");
        _scanner.ScanAll();

        // Delete exactly the attributed copy. Reconciliation repoints the row
        // at the survivor — which leaves the row describing the DELETED copy's
        // bytes unless the survivor is re-parsed, and the survivor rides the
        // mtime fast path forever.
        var before = _messages.GetByMessageId("repoint-dv@x").ShouldNotBeNull();
        File.Delete(Path.Combine(_root, before.MaildirPath, before.MaildirFilename));

        _scanner.ScanAll();                       // repoints location
        _scanner.ScanAll();                       // must re-parse the survivor

        var after = _messages.GetByMessageId("repoint-dv@x").ShouldNotBeNull();
        after.DeletedAt.ShouldBeNull();
        after.Folder.ShouldNotBe(before.Folder);
        after.BodyText.ShouldNotBeNull().ShouldContain(MarkerFor(after));
    }

    [Fact]
    public void Mbsync_new_to_cur_rename_does_not_create_a_duplicate()
    {
        var path = WriteEml("INBOX", "new", "x.host", "first pass", "rename@x");
        _scanner.ScanAll();
        _messages.CountAll().ShouldBe(1);

        // Simulate mbsync renaming new/x.host into cur/x.host:2,S after the user marks it read.
        var newDir = Path.Combine(_root, "INBOX", "cur");
        Directory.CreateDirectory(newDir);
        var renamed = Path.Combine(newDir, "x.host:2,S");
        File.Move(path, renamed);

        var second = _scanner.ScanAll();
        second.Seen.ShouldBe(1);
        second.SoftDeleted.ShouldBe(0);
        _messages.CountAll().ShouldBe(1);
        _messages.GetByMessageId("rename@x").ShouldNotBeNull().MaildirPath.ShouldBe("INBOX/cur");
    }
}
