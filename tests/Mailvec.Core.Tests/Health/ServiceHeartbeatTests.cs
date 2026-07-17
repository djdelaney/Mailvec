using Mailvec.Core.Health;
using Shouldly;

namespace Mailvec.Core.Tests.Health;

/// <summary>
/// Covers the staleness rule and — more importantly — the two distinctions the
/// whole design rests on: unknown-vs-stale, and liveness-vs-progress.
/// </summary>
public class ServiceHeartbeatTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_beat_on_record_is_unknown_not_stale()
    {
        // A fresh DB, or a worker that hasn't beaten yet. Reporting this as
        // stale would paint every first boot red and teach the user to ignore
        // the indicator — the failure mode this whole feature exists to avoid.
        var liveness = ServiceHeartbeat.Classify("indexer", lastBeatAt: null, lastCycleAt: null, intervalSeconds: 60, now: Now);

        liveness.Known.ShouldBeFalse();
        liveness.Stale.ShouldBeFalse();
        liveness.LastBeatAt.ShouldBeNull();
    }

    [Fact]
    public void Beat_without_a_declared_interval_is_unknown()
    {
        // The interval travels WITH the beat so the reader needs no config
        // coupling. A beat lacking one can't be judged, so it must not be
        // guessed at — a hardcoded fallback here is exactly the silent
        // desync the self-describing design exists to prevent.
        var liveness = ServiceHeartbeat.Classify("indexer", Now.AddSeconds(-5), null, intervalSeconds: null, now: Now);

        liveness.Known.ShouldBeFalse();
        liveness.Stale.ShouldBeFalse();
    }

    [Fact]
    public void Fresh_beat_is_live()
    {
        var liveness = ServiceHeartbeat.Classify("embedder", Now.AddSeconds(-10), null, intervalSeconds: 60, now: Now);

        liveness.Known.ShouldBeTrue();
        liveness.Stale.ShouldBeFalse();
    }

    [Theory]
    // Just inside the 3x window (180s at a 60s interval) — a worker that's a
    // little late must not flap red.
    [InlineData(179, false)]
    [InlineData(180, false)]
    // Past it: a service that WAS beating has stopped.
    [InlineData(181, true)]
    [InlineData(600, true)]
    public void Stale_only_after_the_missed_beat_allowance(int ageSeconds, bool expectStale)
    {
        var liveness = ServiceHeartbeat.Classify("indexer", Now.AddSeconds(-ageSeconds), null, intervalSeconds: 60, now: Now);

        liveness.Stale.ShouldBe(expectStale);
        liveness.Known.ShouldBeTrue();
    }

    [Fact]
    public void Staleness_scales_with_the_writers_own_interval()
    {
        // mbsync beats every 600s; 700s is late for a 60s service but perfectly
        // healthy here. Hardcoding one threshold across services would make the
        // slow ones permanently red.
        var mbsync = ServiceHeartbeat.Classify("mbsync", Now.AddSeconds(-700), null, intervalSeconds: 600, now: Now);
        var indexer = ServiceHeartbeat.Classify("indexer", Now.AddSeconds(-700), null, intervalSeconds: 60, now: Now);

        mbsync.Stale.ShouldBeFalse();
        indexer.Stale.ShouldBeTrue();
    }

    [Fact]
    public void Live_beat_with_a_stale_cycle_is_reported_as_live()
    {
        // The wedged-worker case: process alive (beating), work loop not
        // turning (cycle ancient). Liveness must stay honest — Stale=false —
        // and LastCycleAt is what tells the other half of the story. Folding
        // the two together would report a running container as dead and send
        // the operator to the wrong place.
        var liveness = ServiceHeartbeat.Classify(
            "indexer",
            lastBeatAt: Now.AddSeconds(-5),
            lastCycleAt: Now.AddHours(-4),
            intervalSeconds: 60,
            now: Now);

        liveness.Stale.ShouldBeFalse();
        liveness.Known.ShouldBeTrue();
        liveness.LastCycleAt.ShouldBe(Now.AddHours(-4));
    }
}
