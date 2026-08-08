using System.Globalization;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Options;
using Mailvec.Core.Vision;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Health;

/// <summary>
/// Computes the health snapshot exposed by the MCP server's /health endpoint.
/// Pulls counts directly from SQLite (cheap aggregates against indexed columns)
/// and pings Ollama with a short-timeout call. Safe to call on every request —
/// no caching layer; each invocation hits the DB and Ollama once.
/// </summary>
public sealed class HealthService(
    ConnectionFactory connections,
    MetadataRepository metadata,
    IEmbeddingClient ollama,
    IOptions<ArchiveOptions> archiveOpts,
    IOptions<OllamaOptions> ollamaOpts,
    // OCR-pipeline deps are optional so the unit tests (which build a minimal
    // HealthService by hand) keep compiling without wiring vision/OCR. In the
    // MCP and CLI DI graphs all three resolve to real services; when null we
    // report OCR as disabled-with-zero-counts, which is the correct "no signal"
    // reading rather than a crash.
    MessageRepository? messages = null,
    IVisionClient? vision = null,
    IOptions<EmbedderOptions>? embedderOpts = null,
    // Which vision provider is configured, so the reported model identity
    // follows the provider instead of always naming the Ollama one. Optional
    // for the same reason as the deps above; null => the Ollama default.
    IOptions<VisionOptions>? visionOptions = null,
    // Same optional-dep rationale as above. Null => mbsync liveness reports
    // "unknown", which is also the honest answer on a launchd install where
    // no sidecar writes the beat file.
    MbsyncHeartbeatFile? mbsyncHeartbeat = null,
    // Ditto for mbsync's last-successful-sync marker. Separate dep because it
    // is a separate fact written by a separate writer — see MbsyncSyncFile.
    MbsyncSyncFile? mbsyncSync = null)
{
    // The version /health reports. Core's own assembly, not the entry
    // assembly: every Mailvec assembly is stamped from the one repo-wide
    // <Version> in Directory.Build.props, so the value is identical to what
    // serverInfo.version / `mailvec status` report — but Core's stamp stays
    // correct under test hosts too (the entry assembly there is testhost).
    private static readonly string BinaryVersion =
        typeof(HealthService).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Whether OCR looks stalled: work is waiting and nothing has committed
    /// within <see cref="OcrHealthKeys.StalledAfter"/>.
    /// </summary>
    /// <remarks>
    /// Three deliberate non-answers, each of which would otherwise be a false
    /// alarm — and a permanently-lit indicator is one nobody reads:
    /// OCR disabled (nothing should be happening), nothing pending (a drained
    /// queue is the healthy steady state, and the whole point of this signal is
    /// that idle must not read as broken), and no success on record at all
    /// (a fresh deployment that simply hasn't OCR'd anything yet — unknown, not
    /// stale, the same rule the service heartbeats follow).
    /// </remarks>
    private static bool? IsOcrStalled(string? lastSuccessAtRaw, long pending, bool enabled)
    {
        if (!enabled || pending == 0) return false;
        if (!DateTimeOffset.TryParse(
                lastSuccessAtRaw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var lastSuccess))
            return null;
        return DateTimeOffset.UtcNow - lastSuccess > OcrHealthKeys.StalledAfter;
    }

    public async Task<HealthReport> CheckAsync(CancellationToken ct = default)
    {
        var (total, deleted, embedded, chunks, lastIndexedAt) = ReadCounts();

        var schemaModel = metadata.Get("embedding_model");
        var schemaDimRaw = metadata.Get("embedding_dimensions");
        _ = int.TryParse(schemaDimRaw, out var schemaDim);

        var configModel = ollamaOpts.Value.EmbeddingModel;
        var configDim = ollamaOpts.Value.EmbeddingDimensions;

        // v11 widened this beyond the model/dimension names: a stored space id
        // or config hash that disagrees with the current configuration means
        // query vectors and stored document vectors no longer share a space —
        // the same corruption the model check exists for, so it reports (and
        // degrades /health) through the same flag. The /up wire name
        // `embeddings.modelMismatch` is locked and keeps carrying the widened
        // meaning. Absent metadata is unknown, never a mismatch.
        var (cfgSpaceId, cfgConfigHash) = Embedding.EmbeddingSpace.FromOllamaOptions(ollamaOpts.Value);
        var storedSpaceId = metadata.Get(Embedding.EmbeddingSpace.SpaceIdKey);
        var storedConfigHash = metadata.Get(Embedding.EmbeddingSpace.ConfigHashKey);
        var spaceMismatch =
            (storedSpaceId is not null && !string.Equals(storedSpaceId, cfgSpaceId, StringComparison.Ordinal))
            || (storedConfigHash is not null && !string.Equals(storedConfigHash, cfgConfigHash, StringComparison.Ordinal));

        var modelMismatch = (schemaModel is not null
            && (schemaModel != configModel || (schemaDim != 0 && schemaDim != configDim)))
            || spaceMismatch;

        // OCR (vision) is a separate, best-effort pipeline stage. Probe the
        // vision model concurrently with the embedding-Ollama ping so /health
        // doesn't pay two serial round-trips — the compose healthcheck times
        // out at 10s, which is the budget everything in here shares.
        var embOpts = embedderOpts?.Value ?? new EmbedderOptions();
        // The vision model is shared by both OCR passes (scanned PDFs and image
        // attachments); the stage is "on" if either is enabled.
        var pdfOcrEnabled = embOpts.OcrEnabled;
        var imageOcrEnabled = embOpts.ImageOcrEnabled;
        var ocrEnabled = pdfOcrEnabled || imageOcrEnabled;
        var ollamaPing = ollama.PingAsync(ct);
        var visionProbeTask = ocrEnabled && vision is not null
            ? vision.ProbeAsync(ct)
            : null;

        var ollamaReachable = await ollamaPing.ConfigureAwait(false);
        var visionProbe = visionProbeTask is null ? null : await visionProbeTask.ConfigureAwait(false);

        // "Not checkable from this process" is not the same as "broken", and
        // must not read as false: the MCP server deliberately holds no
        // credentials for a hosted provider, so a false here would show a
        // permanent OCR fault on a correctly-configured deployment. Null is the
        // existing "unknown" signal and is the honest answer.
        bool? visionModelAvailable = visionProbe is null
            ? null
            : visionProbe.Status == VisionProbeStatus.NotConfiguredHere
                ? null
                : visionProbe.IsAvailable;

        // A failed embed ping has two very different causes with opposite
        // remediation: the server is down (restart Ollama), or the server is
        // fine and the embedding model was never pulled (`ollama pull ...`).
        // One cheap /api/tags follow-up disambiguates; doctor keys its hints
        // off this. A successful ping implies the model works.
        bool? embeddingModelAvailable;
        if (ollamaReachable)
        {
            embeddingModelAvailable = true;
        }
        else
        {
            // Cap the follow-up at 2s instead of the probe's own 5s. It runs
            // serially after the ping, so against a hang-accepting Ollama
            // (ping eats its full 5s) the old worst case pushed /health to
            // ~10s — the compose healthcheck's own timeout, so the container
            // started failing its healthcheck on a slow Ollama rather than
            // reporting one. 2s loses no information: every scenario where the probe
            // answers (server down → fast failure; model missing → fast tags
            // list; model can't load → tags is metadata, no model load) does
            // so well inside 2s, and a server too hung to list tags reads as
            // null ("can't tell") exactly as the full-length probe would.
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                embeddingModelAvailable = await ollama.IsModelAvailableAsync(probeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                embeddingModelAvailable = null; // probe deadline — same reading as a hung server
            }
        }

        var counts = messages?.OcrCounts(embOpts.ImageOcrMinBytes)
            ?? new OcrStageCounts(0, 0, 0, 0);
        // A disabled sub-pass won't drain its backlog, so don't count it as
        // pending (it would show a queue that never moves). Recovered is
        // historical, shown regardless.
        var pdfPending = pdfOcrEnabled ? counts.PdfPending : 0;
        var imagePending = imageOcrEnabled ? counts.ImagePending : 0;
        var ocr = new OcrHealth(
            Enabled: ocrEnabled,
            // The provider that is actually configured — NOT Ollama's model name
            // unconditionally. Reporting `qwen2.5vl:7b` while OCR runs on a
            // hosted endpoint sent /health and doctor both confidently
            // describing an engine the deployment wasn't using.
            VisionModel: VisionRegistration.Describe(visionOptions?.Value ?? new VisionOptions(), ollamaOpts.Value),
            ModelAvailable: visionModelAvailable,
            Pending: pdfPending + imagePending,
            Recovered: counts.Recovered,
            ImagePending: imagePending,
            ImageRecovered: counts.ImageRecovered,
            LastSuccessAt: metadata.Get(OcrHealthKeys.LastSuccessAt),
            LastFailureAt: metadata.Get(OcrHealthKeys.LastFailureAt),
            ConsecutiveFailures: int.TryParse(
                metadata.Get(OcrHealthKeys.ConsecutiveFailures),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var ocrFails) ? ocrFails : 0,
            LastFailureKind: NullIfEmpty(metadata.Get(OcrHealthKeys.LastFailureKind)),
            Retired: counts.Retired,
            PagesSent: long.TryParse(
                metadata.Get(OcrHealthKeys.PagesSentTotal),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var ocrPages) ? ocrPages : 0,
            // Stall detection keys on DECISIONS, not on text recovered. A
            // backlog of legitimately textless photos drains perfectly while
            // recovering no text at all, and calling that "stalled" would flag a
            // working pass — the OcrMinTextChars floor makes it more common
            // still. Falls back to LastSuccessAt so a deployment upgraded
            // mid-flight, with a success on record but no decision yet, reads
            // sensibly rather than as unknown.
            Stalled: IsOcrStalled(
                metadata.Get(OcrHealthKeys.LastDecisionAt) ?? metadata.Get(OcrHealthKeys.LastSuccessAt),
                pdfPending + imagePending, ocrEnabled));

        var live = Math.Max(total - deleted, 0);
        var coverage = live == 0 ? 0d : (double)embedded / live;
        var backlog = Math.Max(live - embedded, 0);

        var embedder = BuildEmbedderHealth(backlog);

        var services = BuildServiceLiveness();

        var mail = mbsyncSync?.Read() ?? MbsyncSyncFile.Classify(null, null);

        // OCR is deliberately NOT part of the degraded decision. Scanned PDFs are
        // a minority of the corpus and search works fine without them, so a
        // missing vision model or an OCR backlog is informational — surfaced in
        // the Ocr section, never as a /health 503. Broadening
        // the degraded set here would page on a non-critical, best-effort stage.
        //
        // Service liveness is excluded for a DIFFERENT reason, worth stating so
        // nobody "fixes" it: /health is the mcp container's compose healthcheck.
        // A stale indexer or embedder says nothing about whether MCP can serve
        // search — folding it into Status would mark the *mcp* container
        // unhealthy because a *sibling* container died, which is both wrong and
        // actively confusing when triaging. Liveness rides along in Services for
        // a client to render; it never flips the 503.
        //
        // Mail.SyncStale is excluded for exactly that second reason: a sidecar
        // whose syncs keep failing is a real pipeline outage, but it is the
        // MBSYNC container's outage, and MCP can still serve search over every
        // message already indexed. Monitor it with its own query — the Uptime
        // Kuma runbook's consolidated expression has a clause for it.
        var status = (ollamaReachable, modelMismatch, embedder.Stuck) switch
        {
            (false, _, _) => "degraded",
            (_, true, _) => "degraded",
            (_, _, true) => "degraded",
            _ => "ok",
        };

        return new HealthReport(
            Status: status,
            Version: BinaryVersion,
            Database: new DatabaseHealth(
                Path: PathExpansion.Expand(archiveOpts.Value.DatabasePath),
                MessagesTotal: total,
                MessagesDeleted: deleted,
                LastIndexedAt: lastIndexedAt),
            Embeddings: new EmbeddingHealth(
                SchemaModel: schemaModel,
                SchemaDimensions: schemaDim == 0 ? null : schemaDim,
                ConfigModel: configModel,
                ConfigDimensions: configDim,
                ModelMismatch: modelMismatch,
                MessagesEmbedded: embedded,
                CoveragePct: Math.Round(coverage * 100d, 1),
                ChunkCount: chunks),
            Ollama: new OllamaHealth(
                BaseUrl: ollamaOpts.Value.BaseUrl,
                Reachable: ollamaReachable,
                ConfiguredModel: configModel,
                EmbeddingModelAvailable: embeddingModelAvailable),
            Embedder: embedder,
            Ocr: ocr,
            Mail: mail,
            Services: services);
    }

    /// <summary>
    /// Liveness for the three background services that can die independently
    /// of the MCP server. The MCP server itself is deliberately absent: it's
    /// the process answering this call, so its own liveness is implied, and it
    /// stays read-only against the database rather than writing a beat to
    /// state the obvious. See <see cref="ServiceHeartbeat"/>.
    /// </summary>
    private IReadOnlyList<ServiceLiveness> BuildServiceLiveness() =>
    [
        ServiceHeartbeat.Read(metadata, ServiceHeartbeat.Indexer),
        ServiceHeartbeat.Read(metadata, ServiceHeartbeat.Embedder),
        mbsyncHeartbeat?.Read()
            ?? ServiceHeartbeat.Classify(MbsyncHeartbeatFile.Service, null, null, null),
    ];

    /// <summary>
    /// Read the embedder's batch-outcome heartbeat (written by the Embedder
    /// process via <see cref="EmbedderHealthKeys"/>) and decide whether it's
    /// stuck. The keys may be absent on a fresh database or a system that's
    /// never had the embedder run a batch — in that case we report null
    /// timestamps and Stuck=false, which is the correct "no signal yet"
    /// reading rather than a false-positive degraded.
    /// </summary>
    private EmbedderHealth BuildEmbedderHealth(long backlog)
    {
        var consecutiveFailuresRaw = metadata.Get(EmbedderHealthKeys.ConsecutiveFailures);
        _ = int.TryParse(consecutiveFailuresRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var consecutiveFailures);

        var lastSuccessAt = ParseTimestamp(metadata.Get(EmbedderHealthKeys.LastSuccessAt));
        var lastFailureAt = ParseTimestamp(metadata.Get(EmbedderHealthKeys.LastFailureAt));
        var lastFailureKind = metadata.Get(EmbedderHealthKeys.LastFailureKind);
        if (string.IsNullOrEmpty(lastFailureKind)) lastFailureKind = null;

        var stuck = IsStuck(backlog, consecutiveFailures, lastSuccessAt, lastFailureAt);

        return new EmbedderHealth(
            LastSuccessAt: lastSuccessAt,
            LastFailureAt: lastFailureAt,
            ConsecutiveFailures: consecutiveFailures,
            LastFailureKind: lastFailureKind,
            Stuck: stuck);
    }

    /// <summary>
    /// Decide whether the embedder is stuck. Two independent triggers, OR'd:
    ///   1. <c>consecutiveFailures >= StuckThreshold</c> — the fast path for
    ///      quick-failing batches (e.g. SQLite constraint errors that throw
    ///      immediately).
    ///   2. Time-based backstop — there's still work to embed, the most recent
    ///      attempt failed (or none has ever succeeded), and no batch has
    ///      succeeded within <see cref="EmbedderHealthKeys.StuckStaleAfter"/>.
    ///      This catches the slow-failing case where each batch burns minutes
    ///      of Ollama timeout before incrementing the counter, so the count
    ///      alone would take 15+ minutes to trip.
    /// A backlog of 0 is never stuck: a fully-drained embedder with a stale
    /// failure on record is simply idle, not broken.
    /// </summary>
    internal static bool IsStuck(
        long backlog,
        int consecutiveFailures,
        DateTimeOffset? lastSuccessAt,
        DateTimeOffset? lastFailureAt,
        DateTimeOffset? now = null)
    {
        if (consecutiveFailures >= EmbedderHealthKeys.StuckThreshold) return true;
        if (backlog <= 0) return false;

        // The last attempt must have failed — either there's a failure on
        // record newer than the last success, or there's never been a success.
        var lastAttemptFailed = lastFailureAt is not null
            && (lastSuccessAt is null || lastFailureAt >= lastSuccessAt);
        if (!lastAttemptFailed) return false;

        var nowUtc = now ?? DateTimeOffset.UtcNow;
        var staleFor = nowUtc - (lastSuccessAt ?? lastFailureAt!.Value);
        return staleFor >= EmbedderHealthKeys.StuckStaleAfter;
    }

    private static DateTimeOffset? ParseTimestamp(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
    }

    private (long Total, long Deleted, long Embedded, long Chunks, DateTimeOffset? LastIndexedAt) ReadCounts()
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM messages),
              (SELECT COUNT(*) FROM messages WHERE deleted_at IS NOT NULL),
              (SELECT COUNT(*) FROM messages WHERE embedded_at IS NOT NULL AND deleted_at IS NULL),
              (SELECT COUNT(*) FROM chunks),
              (SELECT MAX(indexed_at) FROM messages)
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var lastIndexedRaw = reader.IsDBNull(4) ? null : reader.GetString(4);
        DateTimeOffset? lastIndexedAt = lastIndexedRaw is null
            ? null
            : DateTimeOffset.Parse(lastIndexedRaw, System.Globalization.CultureInfo.InvariantCulture);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), lastIndexedAt);
    }
}

/// <summary>
/// <c>Services</c> carries per-service liveness (indexer / embedder / mbsync).
/// It is informational: it never contributes to <c>Status</c>, because /health
/// is the mcp container's own healthcheck and a dead sibling container must
/// not mark MCP unhealthy. See the comment at the Status switch.
/// <c>Version</c> is the running binary's version (same value as
/// serverInfo.version and `mailvec status`) so a deploy can verify the pinned
/// image tag against what's actually serving, with one /health call.
/// </summary>
/// <summary>
/// The minimal projection of <see cref="HealthReport"/> served by the MCP
/// server's <c>/up</c> endpoint — the one an internet-facing monitor polls.
///
/// <para>The rule for this shape is <b>booleans yes, values no</b>. Everything
/// here answers "is something wrong", and nothing here says what the thing IS.
/// Deliberately absent: the archive's filesystem path, corpus and chunk counts,
/// embedding model identity and dimensions, embedder failure timestamps and
/// kinds, OCR counts, per-service beat timestamps, the last successful sync
/// time, and the Ollama base URL — that last being an internal LAN address, on
/// a host with no authentication of its own. A leaked monitoring credential
/// learns that the embedder is stuck, not how much mail there is, when it
/// arrives, or where any of it lives.</para>
///
/// <para><b>The field names are a wire contract with Uptime Kuma.</b> Seven
/// monitors evaluate JSONata against this body — <c>ollama.reachable</c>,
/// <c>embedder.stuck</c>, <c>embeddings.modelMismatch</c>, <c>mail.syncStale</c>,
/// and <c>services[service='indexer'|'embedder'|'mbsync'].stale</c>. The paths are
/// deliberately identical to <see cref="HealthReport"/>'s so a query written
/// against either endpoint works on both. Renaming or nesting anything here
/// silently breaks a monitor: JSONata resolves the missing path to nothing, and
/// a monitor that can never match its expected value just sits red (or, worse,
/// green-because-nothing-matched, depending on the comparison). See
/// docs/monitoring-uptime-kuma.md.</para>
/// </summary>
public sealed record UpReport(
    string Status,
    string Version,
    UpEmbeddings Embeddings,
    UpOllama Ollama,
    UpEmbedder Embedder,
    UpMail Mail,
    IReadOnlyList<UpServiceLiveness> Services,
    // ADDED, never renamed: existing JSONata paths are untouched, so no
    // monitor breaks. See UpOcr.
    UpOcr? Ocr = null);

/// <summary>
/// Whether OCR has stopped producing text while work is waiting — not when it
/// last succeeded, and not how many pages it has sent.
/// </summary>
/// <remarks>
/// A boolean for the same reason <see cref="UpMail"/> withholds its timestamp:
/// a last-success time polled every minute builds a log of when the user's mail
/// is active. The boolean answers the monitoring question without that.
///
/// Null when the state is genuinely unknown (OCR has never run, so there is no
/// success on record). JSONata resolves null to nothing, which leaves a monitor
/// unmatched rather than falsely red on a fresh deployment — the same
/// "unknown is not stale" rule the service heartbeats follow.
/// </remarks>
public sealed record UpOcr(bool Stalled);

/// <summary>Whether the schema's embedding model disagrees with config — not which model.</summary>
public sealed record UpEmbeddings(bool ModelMismatch);

/// <summary>Whether Ollama answered — not its address, and not which model it serves.</summary>
public sealed record UpOllama(bool Reachable);

/// <summary>Whether the embedder is failing to drain — not since when, or with what error.</summary>
public sealed record UpEmbedder(bool Stuck);

/// <summary>
/// Whether IMAP sync has stopped succeeding — not when it last did.
/// </summary>
/// <remarks>
/// The timestamp is withheld deliberately, and it is the one field on this
/// endpoint whose omission is about the USER rather than the deployment:
/// last-successful-sync times, sampled every minute by a monitor, are a log of
/// when the mailbox is active. The boolean answers the monitoring question
/// completely. <c>Known</c> rides along for the same reason it does on
/// <see cref="UpServiceLiveness"/> — "no marker yet" and "sync is broken" are
/// different answers, and a monitor author needs to tell them apart.
/// </remarks>
public sealed record UpMail(bool Known, bool SyncStale);

/// <summary>
/// Per-service liveness, minus the beat timestamps and cadence that
/// <see cref="ServiceLiveness"/> carries. <c>Known</c> rides along because
/// "unknown" and "stale" are different answers (a fresh database or a
/// just-restarted worker is not a dead one) and a monitor author needs to be
/// able to tell them apart.
/// </summary>
public sealed record UpServiceLiveness(string Service, bool Known, bool Stale);

public sealed record HealthReport(
    string Status,
    string Version,
    DatabaseHealth Database,
    EmbeddingHealth Embeddings,
    OllamaHealth Ollama,
    EmbedderHealth Embedder,
    OcrHealth Ocr,
    MailHealth Mail,
    IReadOnlyList<ServiceLiveness> Services);

/// <summary>
/// Outcome of the mbsync sidecar's last sync attempt — the third signal
/// alongside its liveness beat, written to a separate file by a separate
/// writer. See <see cref="MbsyncSyncFile"/> for why the beat can't carry it
/// and why a beating-but-always-failing sidecar is otherwise invisible.
///
/// <para>Informational, exactly like <c>Services</c>: it never contributes to
/// <see cref="HealthReport.Status"/>. A broken sync is the mbsync container's
/// outage, and /health is the mcp container's own compose healthcheck — see
/// the comment at the Status switch.</para>
///
/// <para><c>Known=false</c> means no marker on record: a fresh deployment, or
/// a macOS launchd install where no sidecar writes one. Reported as unknown
/// rather than stale; render it grey, not red.</para>
/// </summary>
public sealed record MailHealth(
    DateTimeOffset? LastSyncAt,
    int? ExpectedIntervalSeconds,
    bool SyncStale,
    bool Known);

/// <summary>
/// Snapshot of the OCR stage (the embedder's vision pass over both scanned PDFs
/// and image attachments). Purely informational on /health — it never flips
/// Status to degraded. <c>Enabled</c> is true if either the PDF or image OCR
/// pass is on; <c>ModelAvailable</c> is null when OCR is disabled or the probe
/// was skipped, true/false otherwise. <c>Pending</c> / <c>Recovered</c> are
/// pipeline totals; <c>ImagePending</c> / <c>ImageRecovered</c> are the image
/// subset so clients can show the PDF-vs-image split.
/// </summary>
public sealed record OcrHealth(
    bool Enabled,
    string VisionModel,
    bool? ModelAvailable,
    long Pending,
    long Recovered,
    long ImagePending,
    long ImageRecovered,
    // Outcome, as distinct from liveness (ModelAvailable) and progress
    // (Pending). Without these, a drained backlog and a pass that silently
    // fails everything are indistinguishable — see OcrHealthKeys.
    string? LastSuccessAt = null,
    string? LastFailureAt = null,
    int ConsecutiveFailures = 0,
    string? LastFailureKind = null,
    long Retired = 0,
    long PagesSent = 0,
    // True when work is pending and nothing has committed within
    // OcrHealthKeys.StalledAfter. Null when unknowable (no record yet), which
    // is NOT the same as false — same "unknown is not stale" rule the service
    // heartbeats follow, so a fresh deployment doesn't show a false red.
    bool? Stalled = null);

/// <summary>
/// Dynamic "is the embedder making progress" signal — distinct from the
/// static <see cref="EmbeddingHealth"/> (model name / coverage count).
/// Stuck is the load-bearing flag: when true, /health flips to degraded and
/// any monitor wired to that state will fire. ConsecutiveFailures is the
/// raw counter the embedder writes after each attempted batch; the rest are
/// breadcrumbs for the user looking at /health to understand "since when".
/// </summary>
public sealed record EmbedderHealth(
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    int ConsecutiveFailures,
    string? LastFailureKind,
    bool Stuck);

public sealed record DatabaseHealth(
    string Path,
    long MessagesTotal,
    long MessagesDeleted,
    DateTimeOffset? LastIndexedAt);

public sealed record EmbeddingHealth(
    string? SchemaModel,
    int? SchemaDimensions,
    string ConfigModel,
    int ConfigDimensions,
    bool ModelMismatch,
    long MessagesEmbedded,
    double CoveragePct,
    long ChunkCount);

/// <summary>
/// <c>Reachable</c> means "ready to embed" (the ping is a real /api/embed
/// probe, not a liveness GET). When it's false, <c>EmbeddingModelAvailable</c>
/// says why: false = server answered /api/tags but the configured model isn't
/// pulled; true = model is pulled but can't produce an embedding (bad Ollama
/// build, OOM); null = the server itself was unreachable.
/// </summary>
public sealed record OllamaHealth(
    string BaseUrl,
    bool Reachable,
    string ConfiguredModel,
    bool? EmbeddingModelAvailable = null);
