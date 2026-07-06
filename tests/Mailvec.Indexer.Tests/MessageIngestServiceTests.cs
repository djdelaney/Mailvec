using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Indexer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailvec.Indexer.Tests;

/// <summary>
/// Integration tests for the BackgroundService wrapper that drives initial +
/// rescan + watcher-triggered scans. We test the public lifecycle (Start →
/// initial scan completes → file appears → second scan picks it up → Stop).
/// </summary>
public class MessageIngestServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly ConnectionFactory _connections;
    private readonly MessageRepository _messages;

    public MessageIngestServiceTests()
    {
        var temp = Path.Combine(Path.GetTempPath(), "mailvec-ingest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        _root = Path.Combine(temp, "Mail");
        Directory.CreateDirectory(Path.Combine(_root, "INBOX", "cur"));
        Directory.CreateDirectory(Path.Combine(_root, "INBOX", "new"));
        Directory.CreateDirectory(Path.Combine(_root, "INBOX", "tmp"));
        _dbPath = Path.Combine(temp, "archive.sqlite");

        var archiveOpts = Options.Create(new ArchiveOptions { DatabasePath = _dbPath });
        _connections = new ConnectionFactory(archiveOpts);
        new SchemaMigrator(_connections, NullLogger<SchemaMigrator>.Instance).EnsureUpToDate();
        _messages = new MessageRepository(_connections);
    }

    public void Dispose()
    {
        // Scope the clear to THIS database's pool (unique per DataSource) — a
        // global SqliteConnection.ClearAllPools() races with other test classes
        // running in parallel (xUnit parallelizes classes by default), disposing
        // their in-use native connection handles mid-test → ObjectDisposedException
        // or silently-wrong query results.
        using (var conn = _connections.Open())
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(conn);
        }
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task Initial_scan_picks_up_pre_existing_messages()
    {
        WriteEml("INBOX", "cur", "1.host:2,S", "initial body", "init@x");

        var service = BuildService();
        await service.StartAsync(default);

        // ExecuteAsync runs the initial scan synchronously up to its first
        // real await, so by the time we get here the message should be in
        // the DB. But the StartAsync return semantics in BackgroundService
        // depend on the framework version, so allow a brief poll window
        // before asserting — keeps the test stable across .NET releases.
        (await WaitForAsync(() => _messages.CountAll() == 1, TimeSpan.FromSeconds(2))).ShouldBeTrue();
        _messages.GetByMessageId("init@x").ShouldNotBeNull();

        await service.StopAsync(default);
    }

    [Fact]
    public async Task Watcher_pulse_triggers_a_rescan_that_picks_up_new_files()
    {
        var service = BuildService(debounceMs: 100);
        await service.StartAsync(default);
        (await WaitForAsync(() => _messages.CountAll() == 0, TimeSpan.FromSeconds(1))).ShouldBeTrue();

        // After the service is running, drop a new .eml. The FileSystemWatcher
        // pulse should trigger a rescan and pick it up.
        WriteEml("INBOX", "cur", "2.host:2,S", "new body", "new@x");

        // Allow time for: FileSystemWatcher event → debounce window → pulse →
        // ReadPulsesAsync → scanner.ScanAll() → DB visible. macOS FSEvents can
        // silently DROP the Created notification under full-solution parallel
        // load (5 test assemblies hammering the thread pool at once), which left
        // the pulse un-fired and flaked this ~1-in-6 — a longer deadline alone
        // didn't help because the event never arrives. So re-write the file each
        // cycle: every delivered event gives the pulse another chance, and with
        // a 100ms debounce vs 500ms retouch a pulse fires between touches. Still
        // asserts the real chain (watcher pulse → ScanAll → DB row), just resilient
        // to dropped events. ScanAll picks the file up whenever a pulse lands.
        var ingested = await WaitForAsync(
            () => _messages.CountAll() == 1,
            TimeSpan.FromSeconds(20),
            retouch: () => WriteEml("INBOX", "cur", "2.host:2,S", "new body", "new@x"));
        ingested.ShouldBeTrue("watcher never triggered a rescan that ingested the new file");
        _messages.GetByMessageId("new@x").ShouldNotBeNull();

        await service.StopAsync(default);
    }

    [Fact]
    public async Task Recovers_when_the_maildir_root_appears_after_startup()
    {
        // Fresh-install ordering: services installed before mbsync's first
        // sync ever created the Maildir root. The watcher logs "disabled" at
        // startup; pre-fix that was permanent (no retry until process
        // restart). With the timer-tick retry, indexing comes online once
        // the root appears.
        var missingRoot = Path.Combine(Path.GetDirectoryName(_root)!, "NotYetCreated");
        var service = BuildService(rootOverride: missingRoot, scanIntervalSeconds: 1);
        await service.StartAsync(default);

        // Root (and mail) appear after the service is already running.
        var dir = Path.Combine(missingRoot, "INBOX", "cur");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "late.host:2,S"), """
            Message-ID: <late@x>
            Date: Mon, 13 Jan 2025 10:15:00 -0500
            From: alice@example.com
            To: bob@example.com
            Subject: Test
            MIME-Version: 1.0
            Content-Type: text/plain; charset=utf-8

            arrived after startup

            """);

        (await WaitForAsync(() => _messages.CountAll() == 1, TimeSpan.FromSeconds(15)))
            .ShouldBeTrue("indexer never picked up mail from a root created after startup");
        _messages.GetByMessageId("late@x").ShouldNotBeNull();

        await service.StopAsync(default);
    }

    private async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout, Action? retouch = null)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return true;
            retouch?.Invoke();
            await Task.Delay(retouch is null ? 50 : 500);
        }
        return condition();
    }

    private void WriteEml(string folder, string subdir, string filename, string body, string messageId)
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
    }

    private MessageIngestService BuildService(int debounceMs = 200, string? rootOverride = null, int scanIntervalSeconds = 9999)
    {
        var ingestOpts = Options.Create(new IngestOptions { MaildirRoot = rootOverride ?? _root });
        var indexerOpts = Options.Create(new IndexerOptions
        {
            ScanIntervalSeconds = scanIntervalSeconds,   // 9999 = timer effectively disabled
            DebounceMilliseconds = debounceMs,
        });
        var chunks = new ChunkRepository(_connections);
        var syncState = new SyncStateRepository(_connections);

        var scanner = new MaildirScanner(
            ingestOpts,
            new MessageParser(),
            _messages, chunks, syncState,
            _connections,
            NullLogger<MaildirScanner>.Instance);
        var watcher = new MaildirWatcher(ingestOpts, indexerOpts, NullLogger<MaildirWatcher>.Instance);
        var migrator = new SchemaMigrator(_connections, NullLogger<SchemaMigrator>.Instance);

        return new MessageIngestService(
            migrator, scanner, watcher, indexerOpts,
            NullLogger<MessageIngestService>.Instance);
    }
}
