namespace Mailvec.Core.Eval;

/// <summary>
/// Pure information-retrieval metrics. Inputs are an ordered list of result
/// ids (the system's ranking) and a relevance map (id → graded relevance,
/// where grade &gt; 0 means relevant). All metrics return 0 when there are
/// no relevant docs for the query — callers should treat such queries as
/// labeling oversights, not failures.
/// </summary>
public static class EvalMetrics
{
    /// <summary>
    /// Normalized Discounted Cumulative Gain at k. Standard formulation:
    /// DCG = sum(grade_i / log2(rank_i + 1)) over the top-k ranks; IDCG
    /// is DCG of the optimal ranking (relevant docs sorted by grade desc).
    /// Rank-aware (rewards relevant results appearing early) and supports
    /// graded relevance.
    /// </summary>
    public static double NdcgAtK(IReadOnlyList<string> ranked, IReadOnlyDictionary<string, double> grades, int k)
    {
        if (k <= 0 || grades.Count == 0) return 0.0;

        var dcg = 0.0;
        var limit = Math.Min(k, ranked.Count);
        for (var i = 0; i < limit; i++)
        {
            if (grades.TryGetValue(ranked[i], out var g) && g > 0)
                dcg += g / Math.Log2(i + 2); // i is 0-indexed; log2(rank+1) = log2(i+2)
        }

        var idcg = 0.0;
        var topGrades = grades.Values.Where(g => g > 0).OrderByDescending(g => g).Take(k).ToList();
        for (var i = 0; i < topGrades.Count; i++)
            idcg += topGrades[i] / Math.Log2(i + 2);

        return idcg == 0.0 ? 0.0 : dcg / idcg;
    }

    /// <summary>
    /// Mean Reciprocal Rank at k (single-query reciprocal rank; "mean"
    /// happens at the aggregation layer). 1/rank of the first relevant
    /// result, or 0 if none of the top-k are relevant. Ignores grade.
    /// </summary>
    public static double MrrAtK(IReadOnlyList<string> ranked, IReadOnlySet<string> relevant, int k)
    {
        if (k <= 0 || relevant.Count == 0) return 0.0;
        var limit = Math.Min(k, ranked.Count);
        for (var i = 0; i < limit; i++)
        {
            if (relevant.Contains(ranked[i])) return 1.0 / (i + 1);
        }
        return 0.0;
    }

    /// <summary>
    /// Recall at k: fraction of relevant docs that appeared in the top-k.
    /// Ignores grade.
    /// </summary>
    public static double RecallAtK(IReadOnlyList<string> ranked, IReadOnlySet<string> relevant, int k)
    {
        if (k <= 0 || relevant.Count == 0) return 0.0;
        var hits = 0;
        var limit = Math.Min(k, ranked.Count);
        for (var i = 0; i < limit; i++)
        {
            if (relevant.Contains(ranked[i])) hits++;
        }
        return (double)hits / relevant.Count;
    }
}
