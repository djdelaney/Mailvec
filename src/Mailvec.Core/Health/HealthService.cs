using System.Globalization;
using Mailvec.Core.Data;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
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
    OllamaClient ollama,
    IOptions<ArchiveOptions> archiveOpts,
    IOptions<OllamaOptions> ollamaOpts)
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

        var ollamaReachable = await ollama.PingAsync(ct).ConfigureAwait(false);

        var live = Math.Max(total - deleted, 0);
        var coverage = live == 0 ? 0d : (double)embedded / live;

        var embedder = BuildEmbedderHealth();

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
            Embedder: embedder);
    }

    /// <summary>
    /// Read the embedder's batch-outcome heartbeat (written by the Embedder
    /// process via <see cref="EmbedderHealthKeys"/>) and decide whether it's
    /// stuck. The keys may be absent on a fresh database or a system that's
    /// never had the embedder run a batch — in that case we report null
    /// timestamps and Stuck=false, which is the correct "no signal yet"
    /// reading rather than a false-positive degraded.
    /// </summary>
    private EmbedderHealth BuildEmbedderHealth()
    {
        var consecutiveFailuresRaw = metadata.Get(EmbedderHealthKeys.ConsecutiveFailures);
        _ = int.TryParse(consecutiveFailuresRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var consecutiveFailures);

        var lastSuccessAt = ParseTimestamp(metadata.Get(EmbedderHealthKeys.LastSuccessAt));
        var lastFailureAt = ParseTimestamp(metadata.Get(EmbedderHealthKeys.LastFailureAt));
        var lastFailureKind = metadata.Get(EmbedderHealthKeys.LastFailureKind);
        if (string.IsNullOrEmpty(lastFailureKind)) lastFailureKind = null;

        var stuck = consecutiveFailures >= EmbedderHealthKeys.StuckThreshold;

        return new EmbedderHealth(
            LastSuccessAt: lastSuccessAt,
            LastFailureAt: lastFailureAt,
            ConsecutiveFailures: consecutiveFailures,
            LastFailureKind: lastFailureKind,
            Stuck: stuck);
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
    EmbedderHealth Embedder);

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
