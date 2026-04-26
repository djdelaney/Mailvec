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
            MaildirRoot = _root,
            DatabasePath = _dbPath,
        });

        _connections = new ConnectionFactory(archiveOptions);
        new SchemaMigrator(_connections, NullLogger<SchemaMigrator>.Instance).EnsureUpToDate();

        _messages = new MessageRepository(_connections);
        _syncState = new SyncStateRepository(_connections);
        _scanner = new MaildirScanner(
            archiveOptions,
            new MessageParser(),
            _messages,
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
