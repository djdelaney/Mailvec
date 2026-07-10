using System.Diagnostics;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mailvec.Core.Tests.Data;

/// <summary>
/// Pins the cross-connection contention contract every write path leans on:
/// a writer blocked by another connection's held write lock WAITS (the
/// busy-retry loop, bounded by the command timeout) and then THROWS
/// SQLITE_BUSY — it neither fails instantly nor hangs forever — and the same
/// write succeeds once the lock is released. This is what makes "indexer
/// upsert races embedder chunk write" degrade to a retried batch instead of
/// a hang or a crash; it was verified empirically during the concurrency
/// audit but pinned nowhere.
/// </summary>
public sealed class CrossConnectionBusyTests : IDisposable
{
    private readonly string _dir;
    private readonly ConnectionFactory _connections;

    public CrossConnectionBusyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mailvec-busy-tests-" + Guid.NewGuid().ToString("N"));
        _connections = new ConnectionFactory(
            Microsoft.Extensions.Options.Options.Create(new ArchiveOptions
            {
                DatabasePath = Path.Combine(_dir, "archive.sqlite"),
            }))
        {
            // Shrink BOTH timeouts, not just the command timeout: the native
            // busy_timeout sleeps inside a single statement step, so the
            // command-timeout check only runs between busy-timeout quanta and
            // the effective wait rounds UP to a quantum multiple. With the
            // production 5000ms quantum this test measured 10.1s on a slow CI
            // runner (2 quanta) and flaked its ceiling; 250ms quanta against
            // a 1s budget give a deterministic ~1-1.25s wait. The mechanism
            // under test is identical at production values.
            DefaultTimeoutSeconds = 1,
            BusyTimeoutMilliseconds = 250,
        };
        new SchemaMigrator(_connections, NullLogger<SchemaMigrator>.Instance).EnsureUpToDate();
    }

    public void Dispose()
    {
        using (var conn = _connections.Open())
        {
            SqliteConnection.ClearPool(conn);
        }
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private static ParsedMessage Sample(string id) => new(
        MessageId: id, ThreadId: id, Subject: "s", FromAddress: "a@x", FromName: null,
        ToAddresses: [], CcAddresses: [], DateSent: DateTimeOffset.UtcNow, BodyText: "body",
        BodyHtml: null, RawHeaders: $"Message-ID: <{id}>\r\n", SizeBytes: 100, ContentHash: $"h-{id}",
        Attachments: []);

    private static float[] Hot(int i)
    {
        var v = new float[1024];
        v[i % 1024] = 1f;
        return v;
    }

    [Fact]
    public void Blocked_writer_waits_then_throws_busy_and_recovers_after_release()
    {
        var messages = new MessageRepository(_connections);
        var chunks = new ChunkRepository(_connections);
        var now = DateTimeOffset.UtcNow;
        long id = messages.Upsert(Sample("busy@x"), "INBOX", "INBOX/cur", "f", now).Id;

        // Another connection takes and HOLDS the write lock (BEGIN IMMEDIATE
        // acquires it up front — the same transaction shape every Mailvec
        // writer uses, so no deferred-upgrade ambiguity).
        using var blocker = _connections.Open();
        using (var hold = blocker.CreateCommand())
        {
            hold.CommandText = "BEGIN IMMEDIATE;";
            hold.ExecuteNonQuery();
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var ex = Should.Throw<SqliteException>(() =>
                chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "alpha", 1)], [Hot(0)], now));
            sw.Stop();

            ex.SqliteErrorCode.ShouldBe(5); // SQLITE_BUSY
            // It WAITED (the busy-retry loop ran to its ~1s timeout) rather
            // than failing on first contact...
            sw.Elapsed.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(500));
            // ...and it gave up rather than hanging forever (generous ceiling
            // for slow CI; production is the same shape at 30s).
            sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));

            // Nothing was committed by the failed attempt.
            chunks.CountForMessage(id).ShouldBe(0);
        }
        finally
        {
            using var release = blocker.CreateCommand();
            release.CommandText = "ROLLBACK;";
            release.ExecuteNonQuery();
        }

        // Lock released: the identical write now commits — the "retry next
        // poll succeeds" half of the contract.
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "alpha", 1)], [Hot(0)], now);
        chunks.CountForMessage(id).ShouldBe(1);
    }
}
