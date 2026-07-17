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
    // Same optional-dep rationale as above. Null => mbsync liveness reports
    // "unknown", which is also the honest answer on a launchd install where
    // no sidecar writes the beat file.
    MbsyncHeartbeatFile? mbsyncHeartbeat = null)
{
    // The version /health reports. Core's own assembly, not the entry
    // assembly: every Mailvec assembly is stamped from the one repo-wide
    // <Version> in Directory.Build.props, so the value is identical to what
    // serverInfo.version / `mailvec status` report — but Core's stamp stays
    // correct under test hosts too (the entry assembly there is testhost).
    private static readonly string BinaryVersion =
        typeof(HealthService).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public async Task<HealthReport> CheckAsync(CancellationToken ct = default)
    {
        var (total, deleted, embedded, chunks, lastIndexedAt) = ReadCounts();

        var schemaModel = metadata.Get("embedding_model");
        var schemaDimRaw = metadata.Get("embedding_dimensions");
        _ = int.TryParse(schemaDimRaw, out var schemaDim);

        var configModel = ollamaOpts.Value.EmbeddingModel;
        var configDim = ollamaOpts.Value.EmbeddingDimensions;

        var modelMismatch = schemaModel is not null
            && (schemaModel != configModel || (schemaDim != 0 && schemaDim != configDim));

        // OCR (vision) is a separate, best-effort pipeline stage. Probe the
        // vision model concurrently with the embedding-Ollama ping so /health
        // (polled by the tray every 5s) doesn't pay two serial round-trips.
        var embOpts = embedderOpts?.Value ?? new EmbedderOptions();
        // The vision model is shared by both OCR passes (scanned PDFs and image
        // attachments); the stage is "on" if either is enabled.
        var pdfOcrEnabled = embOpts.OcrEnabled;
        var imageOcrEnabled = embOpts.ImageOcrEnabled;
        var ocrEnabled = pdfOcrEnabled || imageOcrEnabled;
        var ollamaPing = ollama.PingAsync(ct);
        var visionProbe = ocrEnabled && vision is not null
            ? vision.IsModelAvailableAsync(ct)
            : null;

        var ollamaReachable = await ollamaPing.ConfigureAwait(false);
        bool? visionModelAvailable = visionProbe is null
            ? null
            : await visionProbe.ConfigureAwait(false);

        // A failed embed ping has two very different causes with opposite
        // remediation: the server is down (restart Ollama), or the server is
        // fine and the embedding model was never pulled (`ollama pull ...`).
        // One cheap /api/tags follow-up disambiguates; doctor and the tray
        // key their hints off this. A successful ping implies the model works.
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
            // ~10s while the tray polls every 5s — permanently overlapping
            // polls. 2s loses no information: every scenario where the probe
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
            VisionModel: ollamaOpts.Value.VisionModel,
            ModelAvailable: visionModelAvailable,
            Pending: pdfPending + imagePending,
            Recovered: counts.Recovered,
            ImagePending: imagePending,
            ImageRecovered: counts.ImageRecovered);

        var live = Math.Max(total - deleted, 0);
        var coverage = live == 0 ? 0d : (double)embedded / live;
        var backlog = Math.Max(live - embedded, 0);

        var embedder = BuildEmbedderHealth(backlog);

        var services = BuildServiceLiveness();

        // OCR is deliberately NOT part of the degraded decision. Scanned PDFs are
        // a minority of the corpus and search works fine without them, so a
        // missing vision model or an OCR backlog is informational — surfaced in
        // the Ocr section and as a tray *warn*, never a /health 503. Broadening
        // the degraded set here would page on a non-critical, best-effort stage.
        //
        // Service liveness is excluded for a DIFFERENT reason, worth stating so
        // nobody "fixes" it: /health is the mcp container's compose healthcheck.
        // A stale indexer or embedder says nothing about whether MCP can serve
        // search — folding it into Status would mark the *mcp* container
        // unhealthy because a *sibling* container died, which is both wrong and
        // actively confusing when triaging. Liveness rides along in Services for
        // a client to render; it never flips the 503.
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
public sealed record HealthReport(
    string Status,
    string Version,
    DatabaseHealth Database,
    EmbeddingHealth Embeddings,
    OllamaHealth Ollama,
    EmbedderHealth Embedder,
    OcrHealth Ocr,
    IReadOnlyList<ServiceLiveness> Services);

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
    long ImageRecovered);

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
