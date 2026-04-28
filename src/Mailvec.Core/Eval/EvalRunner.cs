using Mailvec.Core.Search;

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
    double Recall);

public sealed record EvalModeResult(
    EvalMode Mode,
    int TopK,
    IReadOnlyList<EvalQueryResult> Queries)
{
    public double MeanNdcg => Queries.Count == 0 ? 0.0 : Queries.Average(q => q.Ndcg);
    public double MeanMrr => Queries.Count == 0 ? 0.0 : Queries.Average(q => q.Mrr);
    public double MeanRecall => Queries.Count == 0 ? 0.0 : Queries.Average(q => q.Recall);
}

/// <summary>
/// Runs an <see cref="EvalQuerySet"/> through one of the three search
/// services and collects per-query metrics. Stays out of Console — the
/// CLI layer handles formatting.
/// </summary>
public sealed class EvalRunner(
    KeywordSearchService keyword,
    VectorSearchService vector,
    HybridSearchService hybrid)
{
    public async Task<EvalModeResult> RunAsync(
        EvalQuerySet set,
        EvalMode mode,
        int topK,
        CancellationToken ct = default)
    {
        var results = new List<EvalQueryResult>(set.Queries.Count);
        foreach (var q in set.Queries)
        {
            ct.ThrowIfCancellationRequested();
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
        var ranked = await RankAsync(query.Query, mode, topK, filters, ct).ConfigureAwait(false);

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
            Query: query.Query,
            RelevantCount: query.Relevant.Count,
            RankedMessageIds: ranked,
            RanksOfExpected: ranksOfExpected,
            Ndcg: EvalMetrics.NdcgAtK(ranked, grades, topK),
            Mrr: EvalMetrics.MrrAtK(ranked, relevantSet, topK),
            Recall: EvalMetrics.RecallAtK(ranked, relevantSet, topK));
    }

    private async Task<IReadOnlyList<string>> RankAsync(
        string query, EvalMode mode, int topK, SearchFilters? filters, CancellationToken ct)
    {
        switch (mode)
        {
            case EvalMode.Keyword:
            {
                var hits = keyword.Search(query, topK, filters);
                return hits.Select(h => h.MessageIdHeader).ToList();
            }
            case EvalMode.Semantic:
            {
                // Mirror HybridSearchService's k-inflation when filters are present:
                // vec0 KNN runs before the filter join, so a small k + restrictive
                // filter can return an empty post-filter set.
                var k = (filters is null || filters.IsEmpty) ? Math.Max(100, topK * 5) : Math.Max(500, topK * 50);
                var hits = await vector.SearchAsync(query, topK, k: k, filters, ct).ConfigureAwait(false);
                return hits.Select(h => h.MessageIdHeader).ToList();
            }
            case EvalMode.Hybrid:
            {
                var hits = await hybrid.SearchAsync(query, topK, filters: filters, ct: ct).ConfigureAwait(false);
                return hits.Select(h => h.MessageIdHeader).ToList();
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }
}
