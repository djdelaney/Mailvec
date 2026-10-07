using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mailvec.Core.Eval;

/// <summary>
/// JSON-serializable summary of one or more <see cref="EvalModeResult"/>s,
/// used for <c>--json</c> output and <c>--baseline</c> diffing. Stable shape
/// so old reports stay readable as the eval runner evolves.
/// </summary>
public sealed class EvalReport
{
    public int Version { get; set; } = 2;
    public DateTimeOffset RanAt { get; set; }
    public string? QuerySetPath { get; set; }
    public int TopK { get; set; }

    /// <summary>
    /// Which corpus and vector space produced the numbers (v2). Null when
    /// loading a v1 report — those predate it, and their provenance lives only
    /// in <c>baselines/README.md</c> prose.
    /// </summary>
    public EvalReportProvenance? Provenance { get; set; }

    public List<EvalReportRun> Runs { get; set; } = [];

    public static EvalReport From(
        IEnumerable<EvalModeResult> modeResults, string? querySetPath, int topK,
        EvalReportProvenance? provenance = null) => new()
    {
        Version = 2,
        RanAt = DateTimeOffset.UtcNow,
        QuerySetPath = querySetPath,
        TopK = topK,
        Provenance = provenance,
        Runs = modeResults.Select(EvalReportRun.From).ToList(),
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, this, JsonOpts);
    }

    public static EvalReport Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<EvalReport>(stream, JsonOpts)
            ?? throw new InvalidDataException($"Empty or invalid eval report at {path}.");
    }
}

/// <summary>
/// What an eval report's numbers were measured against. Exists because a
/// report used to record only the query set and top-k, so two snapshots from
/// different embedding models were indistinguishable on disk and an embedding
/// experiment series had to be reconstructed from README prose.
/// </summary>
public sealed class EvalReportProvenance
{
    public string? MailvecVersion { get; set; }

    /// <summary>Database path with the home directory collapsed to <c>~</c>.</summary>
    public string? DatabasePath { get; set; }

    public int MessageCount { get; set; }

    /// <summary>
    /// Chunk count — the only on-disk trace of the chunk size the embedder
    /// used, which is an embed-time setting the CLI never sees.
    /// </summary>
    public int ChunkCount { get; set; }

    /// <summary>Messages still awaiting embedding. Non-zero means the vector leg was measured on a partial corpus.</summary>
    public int UnembeddedCount { get; set; }

    public EvalReportEmbedding? Embedding { get; set; }
}

public sealed class EvalReportEmbedding
{
    public string Profile { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Dimensions { get; set; }
    public string SpaceId { get; set; } = string.Empty;
    public string ConfigHash { get; set; } = string.Empty;

    /// <summary>Ollama manifest digest as stamped by the embedder; null when unobserved or hosted.</summary>
    public string? ModelDigest { get; set; }

    // Text transforms, verbatim; null (omitted) when empty, which is the
    // common case. The config hash already covers them — these are for the
    // human reading the file.
    public string? QueryPrefix { get; set; }
    public string? QuerySuffix { get; set; }
    public string? DocumentPrefix { get; set; }
    public string? DocumentSuffix { get; set; }

    /// <summary>
    /// Whether queries were embedded against a loopback endpoint. A boolean
    /// rather than the URL: committed baselines must not carry a LAN address.
    /// </summary>
    public bool EndpointIsLoopback { get; set; }

    /// <summary>
    /// The database's stored space id / config hash, set only when they
    /// DISAGREE with the active profile above. Semantic and hybrid runs are
    /// refused on disagreement, so this can only appear beside keyword-only
    /// results — and says the profile fields describe the query side alone.
    /// </summary>
    public string? StoredSpaceId { get; set; }
    public string? StoredConfigHash { get; set; }
}

public sealed class EvalReportRun
{
    public EvalMode Mode { get; set; }
    public EvalReportAggregate Aggregate { get; set; } = new();
    public List<EvalReportQuery> Queries { get; set; } = [];

    public static EvalReportRun From(EvalModeResult m) => new()
    {
        Mode = m.Mode,
        Aggregate = new EvalReportAggregate
        {
            Ndcg = m.MeanNdcg,
            Mrr = m.MeanMrr,
            Recall = m.MeanRecall,
            QueryCount = m.Queries.Count,
            NegativeQueryCount = m.NegativeQueries.Count,
            MeanSpecificity = m.MeanSpecificity,
            MeanLatencyMs = m.MeanLatencyMs,
            P50LatencyMs = m.P50LatencyMs,
            P95LatencyMs = m.P95LatencyMs,
        },
        Queries = m.Queries.Select(q => new EvalReportQuery
        {
            Id = q.Id,
            RelevantCount = q.RelevantCount,
            Ndcg = q.Ndcg,
            Mrr = q.Mrr,
            Recall = q.Recall,
            RanksOfExpected = q.RanksOfExpected.ToList(),
            LatencyMs = q.LatencyMs,
            ExpectEmpty = q.ExpectEmpty,
            ReturnedCount = q.ReturnedCount,
            // null (omitted) for normal queries — in-memory it's NaN, which JSON can't encode.
            Specificity = q.ExpectEmpty ? q.Specificity : null,
        }).ToList(),
    };
}

public sealed class EvalReportAggregate
{
    public double Ndcg { get; set; }
    public double Mrr { get; set; }
    public double Recall { get; set; }
    public int QueryCount { get; set; }
    // Negative-query fields default to 0 for historical reports written before
    // expectEmpty support. QueryCount still counts all queries (incl. negatives);
    // Ndcg/Mrr/Recall above are means over the scored (non-negative) queries only.
    public int NegativeQueryCount { get; set; }
    public double MeanSpecificity { get; set; }
    // Latency fields default to 0 when loading historical reports written before timing was captured.
    public double MeanLatencyMs { get; set; }
    public double P50LatencyMs { get; set; }
    public double P95LatencyMs { get; set; }
}

public sealed class EvalReportQuery
{
    public string Id { get; set; } = string.Empty;
    public int RelevantCount { get; set; }
    public double Ndcg { get; set; }
    public double Mrr { get; set; }
    public double Recall { get; set; }
    public List<int> RanksOfExpected { get; set; } = [];
    public double LatencyMs { get; set; }
    public bool ExpectEmpty { get; set; }
    public int ReturnedCount { get; set; }
    public double? Specificity { get; set; }
}
