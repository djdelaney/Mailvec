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
    double LatencyMs,
    bool ExpectEmpty = false,
    int ReturnedCount = 0,
    // Negative-query metric: 1.0 when nothing was returned, scaling to 0.0 as the
    // result count approaches top-k. NaN for normal (non-expectEmpty) queries.
    double Specificity = double.NaN);

public sealed record EvalModeResult(
    EvalMode Mode,
    int TopK,
    IReadOnlyList<EvalQueryResult> Queries)
{
    // Aggregate quality means cover only scored (non-negative) queries — NDCG/MRR/
    // Recall are undefined over an empty gold set, so folding negatives in would
    // drag the means toward 0 for no real reason.
    private IReadOnlyList<EvalQueryResult> Scored => Queries.Where(q => !q.ExpectEmpty).ToList();
    public IReadOnlyList<EvalQueryResult> NegativeQueries => Queries.Where(q => q.ExpectEmpty).ToList();
    public double MeanNdcg => Scored.Count == 0 ? 0.0 : Scored.Average(q => q.Ndcg);
    public double MeanMrr => Scored.Count == 0 ? 0.0 : Scored.Average(q => q.Mrr);
    public double MeanRecall => Scored.Count == 0 ? 0.0 : Scored.Average(q => q.Recall);
    /// <summary>Mean specificity over negative (expectEmpty) queries; 0 if there are none.</summary>
    public double MeanSpecificity => NegativeQueries.Count == 0 ? 0.0 : NegativeQueries.Average(q => q.Specificity);

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

        var returnedInTopK = Math.Min(ranked.Count, topK);

        return new EvalQueryResult(
            Id: query.Id,
            Query: query.Query ?? "",
            RelevantCount: query.Relevant.Count,
            RankedMessageIds: ranked,
            RanksOfExpected: ranksOfExpected,
            Ndcg: EvalMetrics.NdcgAtK(ranked, grades, topK),
            Mrr: EvalMetrics.MrrAtK(ranked, relevantSet, topK),
            Recall: EvalMetrics.RecallAtK(ranked, relevantSet, topK),
            LatencyMs: latencyMs,
            ExpectEmpty: query.ExpectEmpty,
            ReturnedCount: returnedInTopK,
            // Specificity: 1.0 when nothing came back, scaling linearly to 0.0 as the
            // result count fills top-k. Only meaningful for negative queries.
            Specificity: query.ExpectEmpty
                ? 1.0 - Math.Min(1.0, topK <= 0 ? 0.0 : returnedInTopK / (double)topK)
                : double.NaN);
    }
}
