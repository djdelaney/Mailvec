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
        // mimicking sustained SQLITE_BUSY / I/O trouble. The row goes stale,
        // so without the reconciliation guard the live message would be
        // soft-deleted this scan (and purge-able).
        Exec($"CREATE TRIGGER wedge_guard BEFORE UPDATE ON sync_state WHEN new.maildir_full_path = '{path}' BEGIN SELECT RAISE(ABORT, 'injected failure'); END");

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
