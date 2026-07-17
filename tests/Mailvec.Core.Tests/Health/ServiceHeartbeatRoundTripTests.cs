using Mailvec.Core.Data;
using Mailvec.Core.Health;
using Mailvec.Core.Options;
using Mailvec.Core.Tests.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Mailvec.Core.Tests.Health;

/// <summary>
/// The heartbeat's whole point is crossing a process boundary through SQLite,
/// so the interesting coverage is a real write-then-read against the metadata
/// table — the seam where a key-name typo between writer and reader would
/// otherwise show up only in production as a permanently-grey service.
/// </summary>
public class ServiceHeartbeatRoundTripTests
{
    [Fact]
    public void Beat_then_Read_round_trips_through_the_metadata_table()
    {
        using var db = new TempDatabase();
        var metadata = new MetadataRepository(db.Connections);

        ServiceHeartbeat.Beat(metadata, ServiceHeartbeat.Indexer, TimeSpan.FromSeconds(60));

        var liveness = ServiceHeartbeat.Read(metadata, ServiceHeartbeat.Indexer);

        liveness.Known.ShouldBeTrue();
        liveness.Stale.ShouldBeFalse();
        liveness.ExpectedIntervalSeconds.ShouldBe(60);
        liveness.Service.ShouldBe("indexer");
    }

    [Fact]
    public void Read_on_a_fresh_database_is_unknown_for_every_service()
    {
        using var db = new TempDatabase();
        var metadata = new MetadataRepository(db.Connections);

        foreach (var service in new[] { ServiceHeartbeat.Indexer, ServiceHeartbeat.Embedder })
        {
            var liveness = ServiceHeartbeat.Read(metadata, service);
            liveness.Known.ShouldBeFalse();
            liveness.Stale.ShouldBeFalse();
        }
    }

    [Fact]
    public void Services_beat_independently()
    {
        // Separate key namespaces per service: beating one must never make
        // another look alive. A shared key would make a dead embedder
        // invisible for as long as the indexer kept beating.
        using var db = new TempDatabase();
        var metadata = new MetadataRepository(db.Connections);

        ServiceHeartbeat.Beat(metadata, ServiceHeartbeat.Indexer, TimeSpan.FromSeconds(60));

        ServiceHeartbeat.Read(metadata, ServiceHeartbeat.Indexer).Known.ShouldBeTrue();
        ServiceHeartbeat.Read(metadata, ServiceHeartbeat.Embedder).Known.ShouldBeFalse();
    }

    [Fact]
    public async Task HeartbeatService_beats_against_an_unmigrated_database()
    {
        // Regression: HeartbeatService used to assume someone else had already
        // migrated. In the indexer, MessageIngestService yields to the thread
        // pool BEFORE it migrates, so the heartbeat could reach its first beat
        // while the schema still didn't exist: the beat hit "no such table:
        // metadata", was swallowed by the best-effort catch, and a running
        // service then read as unknown for a full interval while every cold
        // start logged a stack trace. Only a real process against a fresh DB
        // surfaced it — a test that pre-migrated could never see it, which is
        // exactly why TempDatabase(migrate: false) is used here.
        using var db = new TempDatabase(migrate: false);
        var metadata = new MetadataRepository(db.Connections);
        var migrator = new SchemaMigrator(db.Connections, NullLogger<SchemaMigrator>.Instance);

        var svc = new HeartbeatService(migrator, metadata, ServiceHeartbeat.Indexer, NullLogger<HeartbeatService>.Instance);
        using var cts = new CancellationTokenSource();

        try
        {
            await svc.StartAsync(cts.Token);

            // Poll rather than assert straight after StartAsync. BackgroundService
            // does NOT guarantee ExecuteAsync has run by the time StartAsync
            // returns — asserting immediately raced the service's own startup and
            // made this test flaky (~1 run in 3) for reasons that had nothing to
            // do with the bug under test.
            await WaitUntil(
                () => ServiceHeartbeat.Read(metadata, ServiceHeartbeat.Indexer).Known,
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            await cts.CancelAsync();
            await svc.StopAsync(CancellationToken.None);
        }

        var liveness = ServiceHeartbeat.Read(metadata, ServiceHeartbeat.Indexer);
        liveness.Known.ShouldBeTrue();
        liveness.Stale.ShouldBeFalse();
        liveness.ExpectedIntervalSeconds.ShouldBe((int)ServiceHeartbeat.BeatInterval.TotalSeconds);
    }

    /// <summary>
    /// Poll until <paramref name="condition"/> holds, swallowing the transient
    /// "no such table" that is the pre-migration state we're waiting out of.
    /// Throws on timeout so a genuine regression fails loudly instead of
    /// hanging the suite.
    /// </summary>
    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (condition()) return;
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // Schema not applied yet — keep waiting.
            }
            await Task.Delay(25);
        }
        throw new TimeoutException($"Condition not met within {timeout.TotalSeconds}s");
    }

    [Fact]
    public void RecordCycle_is_independent_of_the_liveness_beat()
    {
        // The two axes must be writable separately: HeartbeatService stamps
        // liveness on its own timer while the worker stamps cycles from its
        // loop, and neither may clobber the other.
        using var db = new TempDatabase();
        var metadata = new MetadataRepository(db.Connections);

        ServiceHeartbeat.RecordCycle(metadata, ServiceHeartbeat.Embedder);

        // A cycle alone doesn't establish liveness — there's no beat or
        // interval yet, so the service is still "unknown"...
        var afterCycleOnly = ServiceHeartbeat.Read(metadata, ServiceHeartbeat.Embedder);
        afterCycleOnly.Known.ShouldBeFalse();
        // ...but the cycle timestamp survives to be reported alongside it.
        afterCycleOnly.LastCycleAt.ShouldNotBeNull();

        ServiceHeartbeat.Beat(metadata, ServiceHeartbeat.Embedder, TimeSpan.FromSeconds(60));

        var afterBeat = ServiceHeartbeat.Read(metadata, ServiceHeartbeat.Embedder);
        afterBeat.Known.ShouldBeTrue();
        afterBeat.LastCycleAt.ShouldNotBeNull();
        afterBeat.LastBeatAt.ShouldNotBeNull();
    }
}

/// <summary>
/// mbsync is the odd one out — a POSIX-sh sidecar that reports through a file
/// on the Maildir mount instead of the metadata table. These pin the contract
/// the shell writer in the Dockerfile has to satisfy.
/// </summary>
public class MbsyncHeartbeatFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mailvec-hb-" + Guid.NewGuid().ToString("N"));

    private MbsyncHeartbeatFile Build()
    {
        var maildir = Path.Combine(_root, "Fastmail");
        Directory.CreateDirectory(maildir);
        // Fully qualified: this test project has its own Options namespace,
        // which shadows Microsoft.Extensions.Options.
        return new MbsyncHeartbeatFile(
            Microsoft.Extensions.Options.Options.Create(new IngestOptions { MaildirRoot = maildir }));
    }

    [Fact]
    public void Beat_file_lives_beside_the_maildir_root_never_inside_it()
    {
        // Load-bearing: MaildirScanner walks the root, and Maildir++ names
        // folders with a leading dot — a dotfile inside the tree risks being
        // parsed as a folder. This is the assertion that catches someone
        // "tidying" the beat into the maildir.
        var hb = Build();

        hb.Path.ShouldNotBeNull();
        Path.GetDirectoryName(hb.Path).ShouldBe(_root);
        hb.Path!.ShouldNotContain(Path.Combine(_root, "Fastmail"));
    }

    [Fact]
    public void Missing_file_is_unknown_not_stale()
    {
        // The macOS launchd install writes no beat file at all. It must not
        // show a permanent false red.
        var hb = Build();

        var liveness = hb.Read();

        liveness.Known.ShouldBeFalse();
        liveness.Stale.ShouldBeFalse();
    }

    [Fact]
    public void Reads_the_two_line_beat_the_sidecar_writes()
    {
        var hb = Build();
        var now = DateTimeOffset.UtcNow;
        // Exactly the shape of the Dockerfile's `beat()`: ISO-8601 UTC, then
        // the interval.
        File.WriteAllText(hb.Path!, $"{now:yyyy-MM-ddTHH:mm:ssZ}\n600\n");

        var liveness = hb.Read();

        liveness.Known.ShouldBeTrue();
        liveness.Stale.ShouldBeFalse();
        liveness.ExpectedIntervalSeconds.ShouldBe(600);
        liveness.Service.ShouldBe("mbsync");
    }

    [Fact]
    public void An_old_beat_is_stale()
    {
        var hb = Build();
        var old = DateTimeOffset.UtcNow.AddSeconds(-3600);
        File.WriteAllText(hb.Path!, $"{old:yyyy-MM-ddTHH:mm:ssZ}\n600\n");

        hb.Read().Stale.ShouldBeTrue();
    }

    [Fact]
    public void Garbage_content_degrades_to_unknown_rather_than_throwing()
    {
        // A truncated or corrupt beat must not take down /health, which is the
        // compose healthcheck — an unreadable heartbeat would otherwise
        // restart-loop the mcp container.
        var hb = Build();
        File.WriteAllText(hb.Path!, "not-a-timestamp\nnot-a-number\n");

        var liveness = hb.Read();

        liveness.Known.ShouldBeFalse();
        liveness.Stale.ShouldBeFalse();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
