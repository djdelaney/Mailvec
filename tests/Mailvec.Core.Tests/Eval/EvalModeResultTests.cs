using Mailvec.Core.Eval;

namespace Mailvec.Core.Tests.Eval;

/// <summary>
/// Aggregation-method coverage for <see cref="EvalModeResult"/> — the means,
/// the linear-interpolated percentile, and the empty-list fallbacks. Pure
/// arithmetic on the records, no DB or search services.
/// </summary>
public sealed class EvalModeResultTests
{
    private static EvalQueryResult Q(string id, double ndcg, double mrr, double recall, double latency) =>
        new(
            Id: id,
            Query: "q",
            RelevantCount: 1,
            RankedMessageIds: Array.Empty<string>(),
            RanksOfExpected: Array.Empty<int>(),
            Ndcg: ndcg,
            Mrr: mrr,
            Recall: recall,
            LatencyMs: latency);

    [Fact]
    public void Means_average_across_queries()
    {
        var result = new EvalModeResult(EvalMode.Hybrid, 10, new[]
        {
            Q("a", ndcg: 1.0, mrr: 1.0, recall: 1.0, latency: 10),
            Q("b", ndcg: 0.5, mrr: 0.5, recall: 0.5, latency: 30),
            Q("c", ndcg: 0.0, mrr: 0.0, recall: 0.0, latency: 50),
        });

        result.MeanNdcg.ShouldBe(0.5, tolerance: 1e-9);
        result.MeanMrr.ShouldBe(0.5, tolerance: 1e-9);
        result.MeanRecall.ShouldBe(0.5, tolerance: 1e-9);
        result.MeanLatencyMs.ShouldBe(30.0, tolerance: 1e-9);
    }

    [Fact]
    public void Empty_query_list_returns_zero_for_all_aggregates()
    {
        var result = new EvalModeResult(EvalMode.Keyword, 10, Array.Empty<EvalQueryResult>());

        result.MeanNdcg.ShouldBe(0.0);
        result.MeanMrr.ShouldBe(0.0);
        result.MeanRecall.ShouldBe(0.0);
        result.MeanLatencyMs.ShouldBe(0.0);
        result.P50LatencyMs.ShouldBe(0.0);
        result.P95LatencyMs.ShouldBe(0.0);
    }

    [Fact]
    public void Single_query_percentile_returns_that_value()
    {
        var result = new EvalModeResult(EvalMode.Semantic, 10, new[]
        {
            Q("only", ndcg: 0.7, mrr: 0.7, recall: 0.7, latency: 42),
        });

        result.P50LatencyMs.ShouldBe(42);
        result.P95LatencyMs.ShouldBe(42);
    }

    [Fact]
    public void P50_is_median_of_odd_count_list()
    {
        // Sorted: 10, 20, 30 → p50 lands exactly on sorted[1] = 20.
        var result = new EvalModeResult(EvalMode.Hybrid, 10, new[]
        {
            Q("a", 0, 0, 0, 30),
            Q("b", 0, 0, 0, 10),
            Q("c", 0, 0, 0, 20),
        });

        result.P50LatencyMs.ShouldBe(20.0);
    }

    [Fact]
    public void P50_linearly_interpolates_between_neighbours_in_even_count_list()
    {
        // Sorted: 10, 20, 30, 40. p50 = 0.5 * 3 = 1.5 → halfway between 20 and 30.
        var result = new EvalModeResult(EvalMode.Hybrid, 10, new[]
        {
            Q("a", 0, 0, 0, 40),
            Q("b", 0, 0, 0, 10),
            Q("c", 0, 0, 0, 30),
            Q("d", 0, 0, 0, 20),
        });

        result.P50LatencyMs.ShouldBe(25.0, tolerance: 1e-9);
    }

    [Fact]
    public void P95_interpolates_near_the_top_of_the_sorted_list()
    {
        // Sorted: 10, 20, 30, 40, 50. p95 = 0.95 * 4 = 3.8 → 40 + 0.8 * (50 - 40) = 48.
        var result = new EvalModeResult(EvalMode.Hybrid, 10, new[]
        {
            Q("a", 0, 0, 0, 50),
            Q("b", 0, 0, 0, 10),
            Q("c", 0, 0, 0, 40),
            Q("d", 0, 0, 0, 20),
            Q("e", 0, 0, 0, 30),
        });

        result.P95LatencyMs.ShouldBe(48.0, tolerance: 1e-9);
    }
}
