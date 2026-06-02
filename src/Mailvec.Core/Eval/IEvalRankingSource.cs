using Mailvec.Core.Data;
using Mailvec.Core.Search;

namespace Mailvec.Core.Eval;

/// <summary>
/// Pluggable ranking layer for <see cref="EvalRunner"/>. Production wires
/// <see cref="DbEvalRankingSource"/> (the three sealed search services); tests
/// substitute a fake that returns scripted message-id lists so the runner's
/// orchestration (rank-of-expected bookkeeping, latency capture, cancellation,
/// progress callbacks) can be unit-tested without a DB or Ollama.
/// </summary>
public interface IEvalRankingSource
{
    Task<IReadOnlyList<string>> RankAsync(
        string? query,
        EvalMode mode,
        int topK,
        SearchFilters? filters,
        CancellationToken ct);
}

/// <summary>
/// Production <see cref="IEvalRankingSource"/> backed by the keyword, vector,
/// and hybrid search services. Owns the per-mode dispatch and the vector-leg
/// k-inflation for filtered queries (vec0 KNN runs before the filter join, so
/// a small k + restrictive filter can produce an empty post-filter set —
/// mirrors <see cref="HybridSearchService"/>'s own inflation). An empty query
/// routes to query-less browse (date-desc), mirroring <c>search_emails</c>.
/// </summary>
public sealed class DbEvalRankingSource(
    KeywordSearchService keyword,
    VectorSearchService vector,
    HybridSearchService hybrid,
    MessageRepository messages) : IEvalRankingSource
{
    public async Task<IReadOnlyList<string>> RankAsync(
        string? query,
        EvalMode mode,
        int topK,
        SearchFilters? filters,
        CancellationToken ct)
    {
        // Query-less browse: filter-only, date_sent DESC, no ranking. Mode is
        // irrelevant here — there's no query to rank against, so all three
        // modes return the same browse list (the live search_emails behaviour).
        if (string.IsNullOrWhiteSpace(query))
        {
            var browsed = messages.BrowseByFilters(filters ?? new SearchFilters(), topK);
            return browsed.Select(m => m.MessageId).ToList();
        }

        switch (mode)
        {
            case EvalMode.Keyword:
            {
                var hits = keyword.Search(query, topK, filters);
                return hits.Select(h => h.MessageIdHeader).ToList();
            }
            case EvalMode.Semantic:
            {
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
