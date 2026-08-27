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

    // ── Duplicate-instance detection ─────────────────────────────────────────
    //
    // Nothing serializes two indexers against one database and Maildir — the
    // coalescing channel in MessageIngestService is process-local — and two
    // overlapping scans can soft-delete live messages, because each reconciles
    // deletions against the set of paths its own walk enumerated, so mail
    // arriving mid-scan is missing from the older walk's set and reads as
    // deleted. These tests cover making that VISIBLE, which is the
    // deliberate scope: detection, not exclusion.

    private static HeartbeatService Detector(TempDatabase db) =>
        new(new SchemaMigrator(db.Connections, NullLogger<SchemaMigrator>.Instance),
            new MetadataRepository(db.Connections),
            ServiceHeartbeat.Indexer,
            NullLogger<HeartbeatService>.Instance);

    [Fact]
    public void Beat_returns_the_instance_id_it_displaced()
    {
        using var db = new TempDatabase();
        var metadata = new MetadataRepository(db.Connections);

        ServiceHeartbeat.Beat(metadata, ServiceHeartbeat.Indexer, instanceId: "first")
            .ShouldBeNull("nothing had claimed the slot");
        ServiceHeartbeat.Beat(metadata, ServiceHeartbeat.Indexer, instanceId: "second")
            .ShouldBe("first");
    }

    [Fact]
    public void A_restarts_leftover_instance_id_is_not_reported_as_a_duplicate()
    {
        // The previous run's id sits in the slot after any ordinary restart, so
        // a single foreign sighting proves nothing. Reporting on one would fire
        // on every container restart and train the operator to ignore it — the
        // same false-positive trap the Known/Stale split exists to avoid.
        using var db = new TempDatabase();
        var metadata = new MetadataRepository(db.Connections);
        ServiceHeartbeat.Beat(metadata, ServiceHeartbeat.Indexer, instanceId: "the-previous-run");

        var svc = Detector(db);
        svc.WriteBeat();   // claims the slot, displacing the leftover
        svc.WriteBeat();   // sees its own id back — nobody else is beating

        ServiceHeartbeat.Read(metadata, ServiceHeartbeat.Indexer).DuplicateInstanceSeen.ShouldBeFalse();
    }

    [Fact]
    public void An_instance_that_keeps_reclaiming_the_slot_is_reported_as_a_duplicate()
    {
        // A live second process overwrites the key between our beats, so the
        // same foreign id comes back repeatedly. Two consecutive sightings is
        // what separates that from the restart leftover above.
        using var db = new TempDatabase();
        var metadata = new MetadataRepository(db.Connections);
        var svc = Detector(db);

        for (var i = 0; i < 3; i++)
        {
            ServiceHeartbeat.Beat(metadata, ServiceHeartbeat.Indexer, instanceId: "the-other-indexer");
            svc.WriteBeat();
        }

        ServiceHeartbeat.Read(metadata, ServiceHeartbeat.Indexer).DuplicateInstanceSeen.ShouldBeTrue();
    }

    [Fact]
    public void A_duplicate_report_ages_out_once_the_other_process_stops()
    {
        // Self-clearing on the same window as staleness, so stopping the
        // duplicate is the whole remedy — no reset step to forget, and no
        // permanently-red indicator left behind.
        using var db = new TempDatabase();
        var metadata = new MetadataRepository(db.Connections);
        var svc = Detector(db);

        for (var i = 0; i < 3; i++)
        {
            ServiceHeartbeat.Beat(metadata, ServiceHeartbeat.Indexer, instanceId: "the-other-indexer");
            svc.WriteBeat();
        }
        ServiceHeartbeat.Read(metadata, ServiceHeartbeat.Indexer).DuplicateInstanceSeen.ShouldBeTrue();

        var afterWindow = DateTimeOffset.UtcNow
            + ServiceHeartbeat.BeatInterval * ServiceHeartbeat.StaleAfterMissedBeats
            + TimeSpan.FromSeconds(1);
        // Re-beat at the future instant so only the duplicate marker is old.
        ServiceHeartbeat.Beat(metadata, ServiceHeartbeat.Indexer, now: afterWindow);

        ServiceHeartbeat.Read(metadata, ServiceHeartbeat.Indexer, afterWindow)
            .DuplicateInstanceSeen.ShouldBeFalse();
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

    [Fact]
    public void Reports_no_cycle_because_the_beat_no_longer_tracks_one()
    {
        // The beat runs on its own timer, so it says nothing about whether a
        // sync ran. This used to pass the beat timestamp as LastCycleAt too,
        // which was honest only while the sidecar beat after each sync —
        // 6192314 decoupled them and left the alias behind, turning the
        // progress axis into a restatement of LastBeatAt that /health rendered
        // as a real signal. Sync outcome lives in MbsyncSyncFile now.
        var hb = Build();
        File.WriteAllText(hb.Path!, $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\n60\n");

        var liveness = hb.Read();

        liveness.LastBeatAt.ShouldNotBeNull();
        liveness.LastCycleAt.ShouldBeNull();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>
/// mbsync's sync-OUTCOME marker — the third signal, written only on a
/// successful <c>mbsync -a</c>. These pin the contract the Dockerfile's
/// <c>sync_ok()</c> has to satisfy, and the staleness window that decides when
/// a beating-but-failing sidecar gets reported.
/// </summary>
public class MbsyncSyncFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mailvec-sync-" + Guid.NewGuid().ToString("N"));

    private MbsyncSyncFile Build()
    {
        var maildir = Path.Combine(_root, "Fastmail");
        Directory.CreateDirectory(maildir);
        return new MbsyncSyncFile(
            Microsoft.Extensions.Options.Options.Create(new IngestOptions { MaildirRoot = maildir }));
    }

    [Fact]
    public void Marker_lives_beside_the_maildir_root_never_inside_it()
    {
        // Same load-bearing rule as the beat: MaildirScanner walks the root and
        // Maildir++ names folders with a leading dot, so a dotfile in the tree
        // risks being parsed as a folder.
        var sync = Build();

        sync.Path.ShouldNotBeNull();
        Path.GetDirectoryName(sync.Path).ShouldBe(_root);
        sync.Path!.ShouldNotContain(Path.Combine(_root, "Fastmail"));
    }

    [Fact]
    public void Missing_marker_is_unknown_not_stale()
    {
        // Fresh deployment, and every macOS launchd install — no sidecar writes
        // this file at all. A permanent false red is what teaches an operator
        // to ignore the indicator.
        var sync = Build();

        var mail = sync.Read();

        mail.Known.ShouldBeFalse();
        mail.SyncStale.ShouldBeFalse();
    }

    [Fact]
    public void Reads_the_two_line_marker_the_sidecar_writes()
    {
        var sync = Build();
        // Exactly the shape of the Dockerfile's `sync_ok()`: ISO-8601 UTC, then
        // the SYNC interval (not the beat cadence — this file's staleness is a
        // multiple of how often a sync is attempted).
        File.WriteAllText(sync.Path!, $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\n600\n");

        var mail = sync.Read();

        mail.Known.ShouldBeTrue();
        mail.SyncStale.ShouldBeFalse();
        mail.ExpectedIntervalSeconds.ShouldBe(600);
    }

    [Fact]
    public void A_marker_that_stops_advancing_goes_stale()
    {
        // The failure this whole signal exists for: the sidecar beats fine
        // (expired app password, Patterns typo, DNS gone) while every sync
        // fails, so nothing else in the pipeline can tell.
        var sync = Build();
        var old = DateTimeOffset.UtcNow.AddHours(-3);
        File.WriteAllText(sync.Path!, $"{old:yyyy-MM-ddTHH:mm:ssZ}\n600\n");

        sync.Read().SyncStale.ShouldBeTrue();
    }

    [Fact]
    public void Garbage_content_degrades_to_unknown_rather_than_throwing()
    {
        // /health is the mcp container's compose healthcheck — an exception
        // reading a truncated marker would restart-loop it.
        var sync = Build();
        File.WriteAllText(sync.Path!, "not-a-timestamp\nnot-a-number\n");

        var mail = sync.Read();

        mail.Known.ShouldBeFalse();
        mail.SyncStale.ShouldBeFalse();
    }

    [Fact]
    public void A_long_backlog_pull_plus_a_failed_cycle_does_not_trip_the_window()
    {
        // What the window has to absorb is the time BETWEEN successes, not one
        // interval. A 12-minute pull + the 600s interval is 22 minutes before
        // anything has gone wrong; one failed cycle on top pushes past 30. This
        // is why the multiplier is 4 and not StaleAfterMissedBeats.
        MbsyncSyncFile.Classify(DateTimeOffset.UtcNow.AddMinutes(-32), 600).SyncStale.ShouldBeFalse();
        MbsyncSyncFile.Classify(DateTimeOffset.UtcNow.AddMinutes(-45), 600).SyncStale.ShouldBeTrue();
    }

    [Fact]
    public void A_short_sync_interval_does_not_collapse_the_window()
    {
        // docs/future-ideas.md plans a one-minute sync cadence. A bare multiple
        // would give a 4-minute window there, so any pull slower than that
        // would report a working sidecar as broken — the same class of bug as
        // wiring the beat cadence to the sync interval.
        MbsyncSyncFile.Classify(DateTimeOffset.UtcNow.AddMinutes(-20), 60).SyncStale.ShouldBeFalse();
        MbsyncSyncFile.Classify(DateTimeOffset.UtcNow.AddMinutes(-40), 60).SyncStale.ShouldBeTrue();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
