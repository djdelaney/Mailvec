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
    public int Version { get; set; } = 1;
    public DateTimeOffset RanAt { get; set; }
    public string? QuerySetPath { get; set; }
    public int TopK { get; set; }
    public List<EvalReportRun> Runs { get; set; } = [];

    public static EvalReport From(IEnumerable<EvalModeResult> modeResults, string? querySetPath, int topK) => new()
    {
        Version = 1,
        RanAt = DateTimeOffset.UtcNow,
        QuerySetPath = querySetPath,
        TopK = topK,
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
        },
        Queries = m.Queries.Select(q => new EvalReportQuery
        {
            Id = q.Id,
            RelevantCount = q.RelevantCount,
            Ndcg = q.Ndcg,
            Mrr = q.Mrr,
            Recall = q.Recall,
            RanksOfExpected = q.RanksOfExpected.ToList(),
        }).ToList(),
    };
}

public sealed class EvalReportAggregate
{
    public double Ndcg { get; set; }
    public double Mrr { get; set; }
    public double Recall { get; set; }
    public int QueryCount { get; set; }
}

public sealed class EvalReportQuery
{
    public string Id { get; set; } = string.Empty;
    public int RelevantCount { get; set; }
    public double Ndcg { get; set; }
    public double Mrr { get; set; }
    public double Recall { get; set; }
    public List<int> RanksOfExpected { get; set; } = [];
}
