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
            NullLogger<MaildirScanner>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
