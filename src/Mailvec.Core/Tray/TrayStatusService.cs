using Mailvec.Core.Data;
using Mailvec.Core.Health;
using Mailvec.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Tray;

/// <summary>
/// Builds the <see cref="TrayStatus"/> payload consumed by the tray dashboard.
/// Combines:
/// <list type="bullet">
///   <item>the <see cref="HealthService"/> snapshot (DB / Ollama / model match)</item>
///   <item>the <see cref="TrayEventRecorder"/> in-memory ring buffer</item>
///   <item>per-service launchd state from <see cref="LaunchdInspector"/></item>
///   <item>a short SQL query for the recent activity timeline</item>
/// </list>
/// Each method call hits the DB twice (health + recent-events) and shells out
/// four times (launchctl print, in parallel). Total cost is dominated by the
/// launchctl calls (~50ms total on a warm machine) — fast enough to serve at
/// the tray's 5-second poll cadence.
/// </summary>
public sealed class TrayStatusService(
    HealthService health,
    TrayEventRecorder events,
    LaunchdInspector launchd,
    ConnectionFactory connections,
    MetadataRepository metadata,
    MbsyncErrorTail mbsyncErrors,
    IOptions<ArchiveOptions> archiveOpts,
    ILogger<TrayStatusService> logger)
{
    /// <summary>
    /// Detail string the service tile shows when a long-lived agent is
    /// installed-but-unloaded (the state the Pause button leaves it in).
    /// Used as a typed signal between <see cref="ClassifyService"/> and
    /// <see cref="ClassifySeverity"/> — don't free-type the literal in
    /// either place.
    /// </summary>
    internal const string PausedDetail = "paused";

    public async Task<TrayStatus> BuildAsync(CancellationToken ct = default)
    {
        var healthTask = health.CheckAsync(ct);
        var launchdTask = launchd.InspectAllAsync(ct);

        var healthReport = await healthTask.ConfigureAwait(false);
        var launchdMap = await launchdTask.ConfigureAwait(false);

        var dbBytes = TryGetDbBytes(archiveOpts.Value.DatabasePath);
        var schemaVersion = metadata.Get("schema_version") ?? "unknown";

        // mbsync exits 0 even when it can't sync (channel locks, DNS,
        // socket errors), so the launchd-exit-code path below would happily
        // keep the tile green. Re-read the stderr log first and let
        // BuildServices override the mbsync entry if there's a recent
        // error worth showing to the user.
        var mbsyncErr = mbsyncErrors.CheckRecent();
        var services = BuildServices(launchdMap, healthReport, mbsyncErr);
        // Closest signal to a sync-completion time: mbsync's stdout log mtime,
        // which advances on every run that produced output. Same source the
        // recovery check reads, so the dashboard's "last sync" and the tile's
        // recovery stay consistent.
        var lastSyncAt = mbsyncErrors.LastSuccessfulSyncAt();
        var ollama = new TrayOllamaStatus(
            Ok: healthReport.Ollama.Reachable,
            Detail: healthReport.Ollama.Reachable
                ? healthReport.Ollama.ConfiguredModel
                // "unreachable" was previously the blanket not-ready text, which
                // sent users restarting a healthy Ollama when the real problem
                // was a never-pulled model. Mirror HealthService's tri-state.
                : healthReport.Ollama.EmbeddingModelAvailable switch
                {
                    false => $"model not pulled — run `ollama pull {healthReport.Ollama.ConfiguredModel}`",
                    true => $"{healthReport.Ollama.ConfiguredModel} can't load — check Ollama build/memory",
                    null => "unreachable",
                },
            Severity: healthReport.Ollama.Reachable ? "ok" : "error");

        var ocr = new TrayOcrStatus(
            Enabled: healthReport.Ocr.Enabled,
            VisionModel: healthReport.Ocr.VisionModel,
            ModelAvailable: healthReport.Ocr.ModelAvailable,
            Pending: healthReport.Ocr.Pending,
            Recovered: healthReport.Ocr.Recovered,
            ImagePending: healthReport.Ocr.ImagePending,
            ImageRecovered: healthReport.Ocr.ImageRecovered,
            Severity: ClassifyOcr(healthReport.Ocr));

        var sparkline = events.SnapshotSparkline();
        var ratePerMin = events.CurrentRatePerMinute();
        var progress = BuildProgress(healthReport, ratePerMin);
        var severity = ClassifySeverity(healthReport, services, ollama, progress);
        // OCR can only *raise* the floor to warn (a missing vision model is a
        // real but non-critical config gap) — it never overrides an error and
        // never escalates to error itself. Kept out of ClassifySeverity's
        // signature so its existing unit tests stay untouched.
        if (ocr.Severity == "warn" && severity != "error") severity = "warn";
        var recentEvents = ReadRecentEvents();
        var (live, deleted) = (healthReport.Database.MessagesTotal - healthReport.Database.MessagesDeleted,
                               healthReport.Database.MessagesDeleted);

        return new TrayStatus(
            Severity: severity,
            Messages: live,
            Deleted: deleted,
            Embedded: healthReport.Embeddings.MessagesEmbedded,
            EmbedTotal: live,
            Chunks: healthReport.Embeddings.ChunkCount,
            LastIndexedAt: healthReport.Database.LastIndexedAt,
            LastSyncAt: lastSyncAt,
            DbSizeBytes: dbBytes,
            SchemaVersion: schemaVersion,
            Services: services,
            Ollama: ollama,
            Ocr: ocr,
            Progress: progress,
            RecentEvents: recentEvents,
            Sparkline: sparkline);
    }

    /// <summary>
    /// OCR tile severity. A backlog alone is "syncing" (work in progress, not a
    /// problem); a pulled-but-disabled or fully-idle stage is "ok". The only
    /// warn is OCR enabled while the vision model isn't installed — that's a
    /// silent gap where scanned PDFs never become searchable. Never "error":
    /// OCR is best-effort and search works without it.
    /// </summary>
    internal static string ClassifyOcr(OcrHealth ocr)
    {
        if (!ocr.Enabled) return "ok";
        if (ocr.ModelAvailable == false) return "warn";
        if (ocr.Pending > 0) return "syncing";
        return "ok";
    }

    internal static IReadOnlyList<TrayServiceStatus> BuildServices(
            IReadOnlyDictionary<string, LaunchdServiceInfo> launchdMap,
            HealthReport healthReport,
            MbsyncError? mbsyncErr)
    {
        var services = new List<TrayServiceStatus>(4);

        foreach (var label in LaunchdInspector.ServiceLabels)
        {
            launchdMap.TryGetValue(label, out var info);
            info ??= new LaunchdServiceInfo(label, Loaded: false, State: "unknown", Pid: null, LastExitCode: null, Runs: 0);

            var id = label.Replace("com.mailvec.", "", StringComparison.Ordinal);
            var (ok, busy, severity, detail) = ClassifyService(id, info, healthReport);

            if (id == "mbsync")
            {
                // mbsync is a timer-driven agent — "not running" with exit 0 is
                // the healthy state between runs. The completion *time* comes
                // from the stdout log mtime (see LastSuccessfulSyncAt), not from
                // launchd, which has no last-run timestamp.

                // Stderr override: if mbsync wrote an error inside the
                // freshness window, that's the truth — launchd's exit
                // code is meaningless because mbsync returns 0 on lock /
                // socket failures. Locks need user action (warn), the
                // rest are usually transient (also warn).
                if (mbsyncErr is not null)
                {
                    // Second positive recovery signal (complements the stdout
                    // mtime check in MbsyncErrorTail): if the indexer has
                    // ingested a new or changed message *after* the error was
                    // written, mbsync must have delivered mail successfully
                    // since, so the error is stale. The scanner's mtime
                    // fast-path means LastIndexedAt only advances on real
                    // content — never on idle rescans — so this can't
                    // false-positive a green tile onto a genuinely stuck sync.
                    var recoveredSince = healthReport.Database.LastIndexedAt is { } lastIndexed
                        && lastIndexed > mbsyncErr.ObservedAt;
                    if (!recoveredSince)
                    {
                        (ok, busy, severity, detail) = ApplyMbsyncErrorOverride(mbsyncErr);
                    }
                }
            }

            services.Add(new TrayServiceStatus(id, detail, ok, busy, severity));
        }

        return services;
    }

    /// <summary>
    /// Translates a freshly-detected mbsync stderr error into a service-tile
    /// override. We use warn (yellow tile, no error banner) rather than
    /// error (red, banner) for everything except the lock case — single
    /// failed runs are common and self-recover. The lock case is escalated
    /// because it doesn't self-recover.
    /// </summary>
    internal static (bool Ok, bool Busy, string Severity, string Detail) ApplyMbsyncErrorOverride(MbsyncError err)
    {
        var detail = err.Kind switch
        {
            MbsyncErrorKind.Locked  => "stuck on .mbsyncstate.lock — see logs",
            MbsyncErrorKind.Dns     => "DNS lookup failed",
            MbsyncErrorKind.Network => "network error",
            MbsyncErrorKind.Auth    => "auth failed — refresh app password",
            _                       => "recent error — see logs",
        };
        // Locked is the only kind that needs the user to intervene. The
        // others are typically transient — warn but don't escalate so the
        // dashboard's error banner doesn't blare for a single missed run.
        var severity = err.Kind == MbsyncErrorKind.Locked ? "error" : "warn";
        return (Ok: false, Busy: false, Severity: severity, Detail: detail);
    }

    internal static (bool Ok, bool Busy, string Severity, string Detail) ClassifyService(
        string id,
        LaunchdServiceInfo info,
        HealthReport healthReport)
    {
        // mbsync is run-then-exit on a timer. "not running" with exit 0 is the
        // healthy idle state, "not running" with a non-zero exit is a real
        // failure. The other three are long-lived daemons; "not running" is
        // always bad for them.
        var isLongLived = id is "indexer" or "embedder" or "mcp";
        var state = info.State;
        var crashed = info.LastExitCode is > 0;

        if (!info.Loaded)
        {
            // The Pause button (bootout) leaves the plist on disk but
            // unloads the agent; LaunchdInspector flags that case with
            // State == "paused" so we don't blare the red "not installed"
            // banner at a user who just clicked Pause.
            return info.State == "paused"
                ? (false, false, "warn", PausedDetail)
                : (false, false, "error", "not installed");
        }

        var running = state == "running" && info.Pid is not null;

        if (isLongLived)
        {
            if (running)
            {
                // Embedder-specific: a running embedder that's been failing
                // batches back-to-back is the classic silent-regression we
                // need to surface loudly. HealthReport.Embedder.Stuck flips
                // true after N consecutive batch failures (see
                // EmbedderHealthKeys.StuckThreshold). Turning the tile red
                // here propagates through ClassifySeverity to the menu-bar
                // dot. The "busy" syncing path stays for the healthy
                // partial-coverage case.
                if (id == "embedder" && healthReport.Embedder.Stuck)
                {
                    var detail = healthReport.Embedder.LastFailureKind is { } kind
                        ? $"stuck — {healthReport.Embedder.ConsecutiveFailures} failed batches ({kind})"
                        : $"stuck — {healthReport.Embedder.ConsecutiveFailures} failed batches";
                    return (false, false, "error", detail);
                }
                var busy = id == "embedder" && healthReport.Embeddings.CoveragePct < 100.0;
                return (true, busy, busy ? "syncing" : "ok", busy ? "embedding" : "idle");
            }
            return (false, false, "error", crashed ? $"exited {info.LastExitCode}" : "stopped");
        }

        // mbsync — timer-driven, so single failed runs are unalarming.
        // The most common non-zero exit is "channel is locked" when a long
        // run is still holding flock() on .mbsyncstate as the next scheduled
        // run starts. In that case the next run usually succeeds. Treat a
        // single failed run as "warn" (yellow tile, won't trigger the
        // error banner) rather than "error" (red, banner). Only escalate to
        // "error" if the agent has clearly never worked (Runs == 0) or
        // hasn't been loaded.
        if (running) return (true, true, "syncing", "syncing now");
        if (!crashed) return (true, false, "ok", $"idle · {info.Runs} runs");
        return (false, false, "warn", $"last run failed (exit {info.LastExitCode})");
    }

    internal static TrayEmbedProgress? BuildProgress(HealthReport h, int ratePerMin)
    {
        var live = h.Database.MessagesTotal - h.Database.MessagesDeleted;
        var done = h.Embeddings.MessagesEmbedded;
        if (done >= live) return null;
        var remaining = Math.Max(0, live - done);
        var eta = ratePerMin > 0 ? (int)Math.Ceiling((double)remaining / ratePerMin) : 0;
        return new TrayEmbedProgress(done, live, ratePerMin, eta);
    }

    internal static string ClassifySeverity(
        HealthReport h,
        IReadOnlyList<TrayServiceStatus> services,
        TrayOllamaStatus ollama,
        TrayEmbedProgress? progress)
    {
        if (h.Embeddings.ModelMismatch) return "error";
        if (!ollama.Ok) return "error";
        if (services.Any(s => s.Severity == "error")) return "error";
        // Paused trumps the syncing-from-progress signal. With the embedder
        // booted out, embed coverage will sit below 100% indefinitely — the
        // dashboard previously showed a misleading "Syncing" pill in that
        // state. Flip to "warn" so the menu-bar dot reminds the user they're
        // paused without firing the red error banner.
        if (services.Any(s => s.Detail == PausedDetail)) return "warn";
        if (progress is not null) return "syncing";
        return "ok";
    }

    private IReadOnlyList<TrayTimelineEvent> ReadRecentEvents()
    {
        // We display the most-recently-indexed messages and label them all as
        // "indexed" events. The earlier draft sorted by
        // `COALESCE(embedded_at, indexed_at)` to distinguish the two event
        // types, but the COALESCE expression can't use any index — on a
        // 77k-message archive that produced a 5-second sort scan, which is
        // way too slow for an endpoint the tray polls every five seconds.
        //
        // `idx_messages_indexed_at` is a full index on indexed_at, so
        // `ORDER BY indexed_at DESC LIMIT 6` is an O(6) index walk. Embedded
        // events would need a separate index — not worth it: every indexed
        // message gets embedded within a minute on a warm system, so the
        // two streams are visually indistinguishable in a 6-item list.
        try
        {
            using var conn = connections.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                  indexed_at,
                  CASE WHEN embedded_at IS NOT NULL THEN 'embed' ELSE 'indexed' END AS kind,
                  COALESCE(subject, '(no subject)') AS subject,
                  COALESCE(from_address, '') AS from_addr
                FROM messages
                WHERE deleted_at IS NULL
                  AND indexed_at IS NOT NULL
                ORDER BY indexed_at DESC
                LIMIT 6
                """;
            using var reader = cmd.ExecuteReader();
            var list = new List<TrayTimelineEvent>(6);
            while (reader.Read())
            {
                var t = DateTimeOffset.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture);
                var kind = reader.GetString(1);
                var subject = Truncate(reader.GetString(2), 60);
                var from = reader.GetString(3);
                var text = string.IsNullOrEmpty(from) ? subject : $"{subject} — {from}";
                list.Add(new TrayTimelineEvent(
                    Time: t,
                    Kind: kind,
                    Text: text,
                    Agent: kind == "embed" ? "embedder" : "indexer",
                    Live: false,
                    Severity: "ok"));
            }
            return list;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read recent events");
            return [];
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private long TryGetDbBytes(string configured)
    {
        try
        {
            var path = PathExpansion.Expand(configured);
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }
}
