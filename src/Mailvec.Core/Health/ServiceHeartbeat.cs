using System.Globalization;
using Mailvec.Core.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mailvec.Core.Health;

/// <summary>
/// Liveness heartbeats for the pipeline's background services.
///
/// <para><b>Why this exists.</b> Before containers, "is the indexer running?"
/// was answered by shelling out to <c>launchctl print</c>. In the
/// compose deployment there is no launchd, and the MCP server cannot see
/// another container's state without mounting the Docker socket — which would
/// hand a read-only service root on the host. So liveness travels the way
/// everything else between these processes does: through shared state.</para>
///
/// <para><b>Two independent axes — don't collapse them.</b>
/// <list type="bullet">
///   <item><b>Liveness</b> (<see cref="AtKey"/>, written by
///     <see cref="HeartbeatService"/> on its own timer): "this process exists
///     and is scheduling work." Catches the dominant failure — container
///     exited, OOM-killed, crash-looping, never started.</item>
///   <item><b>Progress</b> (<see cref="CycleKey"/>, written by the worker):
///     "the work loop completed a cycle." Catches a live process whose loop
///     has wedged.</item>
/// </list>
/// The liveness beat is deliberately NOT emitted from the work loop. A single
/// Ollama batch or one OCR page render routinely outlives the embedder's 30s
/// poll interval, so a beat-on-cycle-completion design would report a
/// perfectly healthy busy worker as dead — the exact false positive that
/// trains you to ignore the indicator.</para>
///
/// <para><b>Distinct from <see cref="EmbedderHealthKeys"/>.</b> Those are a
/// batch-<i>outcome</i> signal ("is the embedder making progress against its
/// backlog") and are deliberately not written on an idle cycle — an embedder
/// with an empty backlog isn't stuck. That property makes them useless for
/// liveness: an idle embedder and a dead one look identical. These keys answer
/// the other question. Keep all three separate.</para>
///
/// <para><b>Self-describing cadence.</b> Each writer records its own interval
/// alongside the timestamp, so a reader judges staleness without knowing the
/// writer's config. The MCP server binds neither <c>IndexerOptions</c> nor
/// <c>EmbedderOptions</c> (see CLAUDE.md's config table); the alternative was
/// config coupling that would silently desync the moment someone retuned a
/// cadence on one side only.</para>
///
/// <para><b>The MCP server deliberately has no heartbeat.</b> It is read-only
/// against the database, and any caller reading a heartbeat is by definition
/// talking to a live MCP server. A beat would break that invariant to convey
/// nothing. mbsync is file-backed instead — it's a POSIX-sh loop that can't
/// write SQLite; see <see cref="MbsyncHeartbeatFile"/>.</para>
/// </summary>
public static class ServiceHeartbeat
{
    public const string Indexer = "indexer";
    public const string Embedder = "embedder";

    /// <summary>
    /// Cadence of the liveness beat. A constant, not an option: it's decoupled
    /// from every worker's work cadence on purpose (that's the whole point),
    /// so there is nothing deployment-specific to tune. 60s gives a 3-minute
    /// detection window at <see cref="StaleAfterMissedBeats"/> — fast enough
    /// to notice a dead container, slow enough that the write is free
    /// (two metadata rows per minute per service).
    /// </summary>
    public static readonly TimeSpan BeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many missed beats before a service reads as stale. 1x would flap on
    /// any scheduling jitter; 3x still surfaces a dead worker within three
    /// minutes at <see cref="BeatInterval"/>.
    /// </summary>
    public const int StaleAfterMissedBeats = 3;

    public static string AtKey(string service) => $"heartbeat.{service}.at";

    public static string IntervalKey(string service) => $"heartbeat.{service}.interval_s";

    public static string CycleKey(string service) => $"heartbeat.{service}.cycle_at";

    /// <summary>
    /// Identity of the process currently beating for this service.
    /// </summary>
    /// <remarks>
    /// Exists to make a duplicate worker VISIBLE. Nothing serializes two
    /// indexers against one database and Maildir — the coalescing channel in
    /// <c>MessageIngestService</c> is process-local — and two overlapping scans
    /// can soft-delete live messages, because each stamps every seen file's
    /// <c>sync_state.last_seen_at</c> to its own start time and then reconciles
    /// deletions against it. Compose won't start two of a service and this
    /// machine runs none, so the reachable case is a macOS launchd install plus
    /// a <c>dotnet run</c> alongside it.
    ///
    /// Detection, not exclusion, and deliberately so. Both processes write this
    /// same key, so reading back an id that isn't yours proves another one is
    /// beating — one metadata row, no lock-file semantics to get right across
    /// bind mounts and crashes. **Do not escalate this to refuse-to-start**: a
    /// container restarting under <c>restart: unless-stopped</c> comes back
    /// while the previous instance's beat is still fresh and would refuse to
    /// boot, turning a diagnostic into the kind of wedge it was added to warn
    /// about.
    /// </remarks>
    public static string InstanceKey(string service) => $"heartbeat.{service}.instance";

    /// <summary>
    /// When a duplicate instance was last confirmed, so READERS can see it too.
    /// </summary>
    /// <remarks>
    /// Detection is structurally local to the beating process — it is the only
    /// one that knows its own id, and a reader looking at
    /// <see cref="InstanceKey"/> sees a single value that tells it nothing. So
    /// the detector records the finding here and readers judge it by freshness,
    /// exactly like the beat itself. That also makes it self-clearing: stop the
    /// duplicate and the marker ages out within the same window, with no reset
    /// step for an operator to forget.
    /// </remarks>
    public static string DuplicateSeenKey(string service) => $"heartbeat.{service}.duplicate_seen_at";

    /// <summary>
    /// Stamp the liveness beat. Called only by <see cref="HeartbeatService"/>.
    /// Returns the instance id found in the database immediately BEFORE this
    /// beat claimed it, so the caller can tell whether another process is
    /// beating for the same service; null when the slot was unclaimed.
    /// </summary>
    public static string? Beat(
        MetadataRepository metadata,
        string service,
        TimeSpan? interval = null,
        DateTimeOffset? now = null,
        string? instanceId = null)
    {
        var seconds = (int)(interval ?? BeatInterval).TotalSeconds;
        string? previousInstance = null;
        if (instanceId is not null)
        {
            previousInstance = metadata.Get(InstanceKey(service));
            metadata.Set(InstanceKey(service), instanceId);
        }
        metadata.Set(AtKey(service), Iso(now));
        metadata.Set(IntervalKey(service), seconds.ToString(CultureInfo.InvariantCulture));
        return previousInstance;
    }

    /// <summary>
    /// Stamp "the work loop completed a cycle". Unconditional — including when
    /// the cycle did nothing, and including when it failed.
    ///
    /// <para>Doing no work is the normal state on a quiet mailbox, and it is
    /// the case that most needs this signal: <c>messages.indexed_at</c> only
    /// advances when real mail arrives (the scanner's mtime fast-path skips
    /// unchanged files by design), so without this, "quiet inbox" and "indexer
    /// wedged" are indistinguishable.</para>
    ///
    /// <para>Failing is likewise still a completed cycle: a scan that throws
    /// is a <i>sick</i> worker, not an absent one, and the two want different
    /// responses. The failure surfaces through the log and, for the embedder,
    /// through <see cref="EmbedderHealthKeys"/>; conflating it with liveness
    /// sends you hunting a dead container that's actually running.</para>
    /// </summary>
    public static void RecordCycle(MetadataRepository metadata, string service, DateTimeOffset? now = null)
        => metadata.Set(CycleKey(service), Iso(now));

    /// <summary>
    /// Read a service's liveness. Returns <c>Known=false</c> when there's no
    /// beat on record — a fresh database, or a worker that hasn't completed
    /// its first beat. That is reported as unknown, never stale: absence of a
    /// signal isn't evidence of death, and a false red on first boot teaches
    /// the user to ignore the indicator.
    /// </summary>
    public static ServiceLiveness Read(MetadataRepository metadata, string service, DateTimeOffset? now = null)
    {
        var at = ParseTimestamp(metadata.Get(AtKey(service)));
        var cycle = ParseTimestamp(metadata.Get(CycleKey(service)));
        var duplicateSeen = ParseTimestamp(metadata.Get(DuplicateSeenKey(service)));
        _ = int.TryParse(metadata.Get(IntervalKey(service)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval);

        return Classify(service, at, cycle, interval > 0 ? interval : null, now, duplicateSeen);
    }

    /// <summary>
    /// The single definition of "stale", shared by the metadata-backed services
    /// and the file-backed mbsync beat so the three transports can't drift.
    /// </summary>
    internal static ServiceLiveness Classify(
        string service,
        DateTimeOffset? lastBeatAt,
        DateTimeOffset? lastCycleAt,
        int? intervalSeconds,
        DateTimeOffset? now = null,
        DateTimeOffset? duplicateSeenAt = null)
    {
        if (lastBeatAt is null || intervalSeconds is null)
            return new ServiceLiveness(service, null, lastCycleAt, intervalSeconds, Stale: false, Known: false);

        var nowUtc = now ?? DateTimeOffset.UtcNow;
        var window = TimeSpan.FromSeconds(intervalSeconds.Value * StaleAfterMissedBeats);
        var stale = nowUtc - lastBeatAt.Value > window;
        // Same window as staleness, so the marker ages out on its own once the
        // duplicate stops beating.
        var duplicate = duplicateSeenAt is { } seen && nowUtc - seen <= window;
        return new ServiceLiveness(service, lastBeatAt, lastCycleAt, intervalSeconds, stale, Known: true, duplicate);
    }

    private static string Iso(DateTimeOffset? now)
        => (now ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTimestamp(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t
            : null;
    }
}

/// <summary>
/// Liveness of one background service.
///
/// <para><c>Known=false</c> means "no beat on record" (fresh DB, or the worker
/// hasn't beaten yet) — deliberately distinct from <c>Stale=true</c>, which is
/// a positive assertion that a service which <i>was</i> beating has stopped.
/// Render them differently: unknown is grey, stale is red.</para>
///
/// <para><c>LastCycleAt</c> is the other axis: a fresh <c>LastBeatAt</c> with a
/// long-stale <c>LastCycleAt</c> means the process is up but its work loop
/// isn't turning. Null when the service reports liveness but not cycles.</para>
/// </summary>
public sealed record ServiceLiveness(
    string Service,
    DateTimeOffset? LastBeatAt,
    DateTimeOffset? LastCycleAt,
    int? ExpectedIntervalSeconds,
    bool Stale,
    bool Known,
    /// <summary>
    /// Another process was recently confirmed to be beating for this service.
    /// Two indexers against one Maildir can soft-delete live messages, so this
    /// is a misconfiguration to fix, not a state to tolerate. Defaults false —
    /// only the metadata-backed services detect it.
    /// </summary>
    bool DuplicateInstanceSeen = false);

/// <summary>
/// Emits the liveness beat for one service on its own timer, independent of
/// whatever the worker's main loop is doing. Hosted alongside the worker in
/// the Indexer and Embedder processes.
///
/// <para>Being a separate <see cref="BackgroundService"/> is the design, not
/// an implementation detail: it means a long Ollama batch or a multi-minute
/// full scan can't starve the beat and fake a dead service. What it proves is
/// bounded and honest — the process is alive and its scheduler is responsive.
/// Whether the work loop is actually turning is <see cref="ServiceHeartbeat.CycleKey"/>'s
/// job.</para>
/// </summary>
public sealed class HeartbeatService(
    SchemaMigrator migrator,
    MetadataRepository metadata,
    string service,
    ILogger<HeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ensure the schema before the first beat. The worker in this same
        // process also migrates, but it yields to the thread pool first, so
        // the host starts US before its migration has necessarily finished —
        // and the immediate beat below would then hit "no such table:
        // metadata" on a fresh database, lose the first beat, and report a
        // running service as unknown for a full interval while logging a
        // stack trace on every cold start. SchemaMigrator is built for
        // exactly this (BEGIN IMMEDIATE; whoever loses the race re-reads the
        // version inside the lock), which is why the MCP, indexer and
        // embedder already each call it independently.
        migrator.EnsureUpToDate();

        // Beat once immediately so a freshly-started service doesn't read as
        // "unknown" for a whole interval after boot.
        WriteBeat();

        using var timer = new PeriodicTimer(ServiceHeartbeat.BeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                WriteBeat();
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    /// <summary>
    /// This process's claim on <see cref="ServiceHeartbeat.InstanceKey"/>.
    /// Per-process and not persisted: it only has to be unique among the
    /// processes beating concurrently.
    /// </summary>
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The last foreign instance id we complained about, so a duplicate that
    /// keeps beating produces one log line per offending process rather than
    /// one per beat, forever. <see cref="HealthService"/> is what surfaces the
    /// ongoing state; the log is the timestamped "it started here".
    /// </summary>
    private string? _reportedForeignInstance;

    /// <summary>
    /// Foreign id seen on the previous beat. Two consecutive sightings of the
    /// same id is what distinguishes a live duplicate from the harmless
    /// leftover a restart finds.
    /// </summary>
    private string? _lastForeignInstance;

    internal void WriteBeat()
    {
        try
        {
            var previous = ServiceHeartbeat.Beat(metadata, service, instanceId: _instanceId);

            // A foreign id means another process wrote this key between our
            // beats. Normal restarts leave the PREVIOUS run's id here and we
            // simply claim it, so a single sighting is not enough — we only
            // report an id that keeps coming back, i.e. one still being written.
            if (previous is not null && previous != _instanceId)
            {
                if (previous == _reportedForeignInstance) return;
                if (_lastForeignInstance == previous)
                {
                    _reportedForeignInstance = previous;
                    metadata.Set(ServiceHeartbeat.DuplicateSeenKey(service), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    logger.LogError(
                        "Another {Service} process appears to be running against this database (instance {Foreign}, " +
                        "ours is {Ours}). Two indexers scanning one Maildir can soft-delete live messages: their scans " +
                        "are only serialized within a process. Stop one of them — check for launchd agents " +
                        "(`launchctl list | grep mailvec`) alongside a `dotnet run`, or a second container.",
                        service, previous, _instanceId);
                }
                _lastForeignInstance = previous;
            }
            else
            {
                _lastForeignInstance = null;
                _reportedForeignInstance = null;
            }
        }
        catch (Exception ex)
        {
            // Never let a heartbeat write take down the process it exists to
            // report on. A missed beat degrades to a false "stale" reading,
            // which is strictly better than killing a healthy worker because
            // it couldn't announce that it was healthy.
            logger.LogWarning(ex, "Failed to write {Service} heartbeat", service);
        }
    }
}
