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
    IOptions<EmbedderOptions>? embedderOpts = null)
{
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

        // OCR is deliberately NOT part of the degraded decision. Scanned PDFs are
        // a minority of the corpus and search works fine without them, so a
        // missing vision model or an OCR backlog is informational — surfaced in
        // the Ocr section and as a tray *warn*, never a /health 503. Broadening
        // the degraded set here would page on a non-critical, best-effort stage.
        var status = (ollamaReachable, modelMismatch, embedder.Stuck) switch
        {
            (false, _, _) => "degraded",
            (_, true, _) => "degraded",
            (_, _, true) => "degraded",
            _ => "ok",
        };

        return new HealthReport(
            Status: status,
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
                ConfiguredModel: configModel),
            Embedder: embedder,
            Ocr: ocr);
    }

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

public sealed record HealthReport(
    string Status,
    DatabaseHealth Database,
    EmbeddingHealth Embeddings,
    OllamaHealth Ollama,
    EmbedderHealth Embedder,
    OcrHealth Ocr);

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

public sealed record OllamaHealth(
    string BaseUrl,
    bool Reachable,
    string ConfiguredModel);
