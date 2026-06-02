using System.Diagnostics;

namespace Mailvec.Core.Eval;

public enum EvalMode { Keyword, Semantic, Hybrid }

public sealed record EvalQueryResult(
    string Id,
    string Query,
    int RelevantCount,
    IReadOnlyList<string> RankedMessageIds,
    IReadOnlyList<int> RanksOfExpected,  // 1-indexed; 0 = not in top-k. Same order as the query's Relevant list.
    double Ndcg,
    double Mrr,
    double Recall,
    double LatencyMs);

public sealed record EvalModeResult(
    EvalMode Mode,
    int TopK,
    IReadOnlyList<EvalQueryResult> Queries)
{
    public double MeanNdcg => Queries.Count == 0 ? 0.0 : Queries.Average(q => q.Ndcg);
    public double MeanMrr => Queries.Count == 0 ? 0.0 : Queries.Average(q => q.Mrr);
    public double MeanRecall => Queries.Count == 0 ? 0.0 : Queries.Average(q => q.Recall);

    public double MeanLatencyMs => Queries.Count == 0 ? 0.0 : Queries.Average(q => q.LatencyMs);
    public double P50LatencyMs => Percentile(Queries.Select(q => q.LatencyMs), 0.50);
    public double P95LatencyMs => Percentile(Queries.Select(q => q.LatencyMs), 0.95);

    /// <summary>
    /// Linear-interpolated percentile. For small N (~10 eval queries) this is more
    /// honest than nearest-rank — the gap between sorted[i] and sorted[i+1] can be
    /// large enough that nearest-rank lies about both.
    /// </summary>
    private static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0.0;
        if (sorted.Length == 1) return sorted[0];
        var rank = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}

/// <summary>
/// Runs an <see cref="EvalQuerySet"/> through an <see cref="IEvalRankingSource"/>
/// and collects per-query metrics. The ranking source is the seam: production
/// wires <see cref="DbEvalRankingSource"/> (the three sealed search services);
/// tests substitute a fake to unit-test the orchestration without a DB.
/// Stays out of Console — the CLI layer handles formatting.
/// </summary>
public sealed class EvalRunner(IEvalRankingSource ranking)
{
    public async Task<EvalModeResult> RunAsync(
        EvalQuerySet set,
        EvalMode mode,
        int topK,
        Action<int, int, string>? onQueryStart = null,
        CancellationToken ct = default)
    {
        var results = new List<EvalQueryResult>(set.Queries.Count);
        for (var i = 0; i < set.Queries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var q = set.Queries[i];
            onQueryStart?.Invoke(i, set.Queries.Count, q.Id);
            results.Add(await RunOneAsync(q, mode, topK, ct).ConfigureAwait(false));
        }
        return new EvalModeResult(mode, topK, results);
    }

    public async Task<EvalQueryResult> RunOneAsync(
        EvalQuery query,
        EvalMode mode,
        int topK,
        CancellationToken ct = default)
    {
        var filters = query.Filters?.ToSearchFilters();

        // Time only the actual ranking call. Filter/grade bookkeeping below is
        // CPU-only and would distort sub-100ms measurements.
        var t0 = Stopwatch.GetTimestamp();
        var ranked = await ranking.RankAsync(query.Query, mode, topK, filters, ct).ConfigureAwait(false);
        var latencyMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        var grades = new Dictionary<string, double>(query.Relevant.Count, StringComparer.Ordinal);
        foreach (var r in query.Relevant) grades[r.MessageId] = r.Grade;
        var relevantSet = new HashSet<string>(grades.Keys, StringComparer.Ordinal);

        var ranksOfExpected = new int[query.Relevant.Count];
        for (var i = 0; i < query.Relevant.Count; i++)
        {
            var id = query.Relevant[i].MessageId;
            ranksOfExpected[i] = 0;
            for (var r = 0; r < ranked.Count; r++)
            {
                if (string.Equals(ranked[r], id, StringComparison.Ordinal))
                {
                    ranksOfExpected[i] = r + 1;
                    break;
                }
            }
        }

        return new EvalQueryResult(
            Id: query.Id,
            Query: query.Query ?? "",
            RelevantCount: query.Relevant.Count,
            RankedMessageIds: ranked,
            RanksOfExpected: ranksOfExpected,
            Ndcg: EvalMetrics.NdcgAtK(ranked, grades, topK),
            Mrr: EvalMetrics.MrrAtK(ranked, relevantSet, topK),
            Recall: EvalMetrics.RecallAtK(ranked, relevantSet, topK),
            LatencyMs: latencyMs);
    }
}
