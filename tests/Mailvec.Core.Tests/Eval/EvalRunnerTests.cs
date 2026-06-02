using Mailvec.Core.Eval;
using Mailvec.Core.Search;

namespace Mailvec.Core.Tests.Eval;

/// <summary>
/// Unit tests for <see cref="EvalRunner"/>'s orchestration logic, using a
/// scripted <see cref="IEvalRankingSource"/> fake to bypass the DB and Ollama.
/// Covers: rank-of-expected bookkeeping, latency capture, cancellation,
/// progress callbacks, metric wiring against <see cref="EvalMetrics"/>, and
/// per-mode dispatch (so the production <see cref="DbEvalRankingSource"/>
/// switch can drift without breaking the runner contract).
/// </summary>
public sealed class EvalRunnerTests
{
    private sealed class FakeRankingSource : IEvalRankingSource
    {
        public List<RankCall> Calls { get; } = new();
        public Func<RankCall, IReadOnlyList<string>>? Script { get; set; }
        public TimeSpan? Delay { get; set; }
        public CancellationToken ObservedLastToken { get; private set; }

        public async Task<IReadOnlyList<string>> RankAsync(
            string? query, EvalMode mode, int topK, SearchFilters? filters, CancellationToken ct)
        {
            var call = new RankCall(query ?? "", mode, topK, filters);
            Calls.Add(call);
            ObservedLastToken = ct;
            if (Delay is { } d) await Task.Delay(d, ct).ConfigureAwait(false);
            return Script?.Invoke(call) ?? Array.Empty<string>();
        }
    }

    private sealed record RankCall(string Query, EvalMode Mode, int TopK, SearchFilters? Filters);

    private static EvalQuery Query(string id, string text, params string[] relevant) => new()
    {
        Id = id,
        Query = text,
        Relevant = relevant.Select(r => new RelevantEntry(r)).ToList(),
    };

    private static EvalQuerySet Set(params EvalQuery[] queries) =>
        new() { Queries = queries.ToList() };

    [Fact]
    public async Task Computes_rank_of_expected_as_one_indexed_position_with_zero_for_misses()
    {
        var fake = new FakeRankingSource
        {
            Script = _ => new[] { "x@a", "expected-a@x", "y@a", "expected-c@x" },
        };
        var runner = new EvalRunner(fake);
        var q = Query("q1", "anything", "expected-a@x", "expected-b@x", "expected-c@x");

        var r = await runner.RunOneAsync(q, EvalMode.Hybrid, topK: 10);

        // RanksOfExpected preserves the order of query.Relevant, 1-indexed,
        // 0 for misses. expected-a is at position 2, expected-b is missing,
        // expected-c is at position 4.
        r.RanksOfExpected.ShouldBe(new[] { 2, 0, 4 });
        r.RelevantCount.ShouldBe(3);
        r.RankedMessageIds.ShouldBe(new[] { "x@a", "expected-a@x", "y@a", "expected-c@x" });
    }

    [Fact]
    public async Task Metrics_match_EvalMetrics_for_the_scripted_ranking()
    {
        var ranked = new[] { "good@x", "miss@x", "good2@x" };
        var fake = new FakeRankingSource { Script = _ => ranked };
        var runner = new EvalRunner(fake);
        var q = Query("q", "x", "good@x", "good2@x");

        var r = await runner.RunOneAsync(q, EvalMode.Keyword, topK: 10);

        // Same inputs the runner builds internally — grades all = 1.0 for the
        // default RelevantEntry constructor; relevant set is the two ids.
        var grades = new Dictionary<string, double> { ["good@x"] = 1.0, ["good2@x"] = 1.0 };
        var relevantSet = new HashSet<string> { "good@x", "good2@x" };
        r.Ndcg.ShouldBe(EvalMetrics.NdcgAtK(ranked, grades, 10), tolerance: 1e-12);
        r.Mrr.ShouldBe(EvalMetrics.MrrAtK(ranked, relevantSet, 10), tolerance: 1e-12);
        r.Recall.ShouldBe(EvalMetrics.RecallAtK(ranked, relevantSet, 10), tolerance: 1e-12);
    }

    [Fact]
    public async Task Captures_latency_of_the_ranking_call_only()
    {
        // Floor the assert well below the delay so CI jitter doesn't flake;
        // the goal is "latency is non-trivially > 0", not microsecond accuracy.
        var fake = new FakeRankingSource { Delay = TimeSpan.FromMilliseconds(50) };
        var runner = new EvalRunner(fake);
        var q = Query("q", "x", "expected@x");

        var r = await runner.RunOneAsync(q, EvalMode.Keyword, topK: 10);

        r.LatencyMs.ShouldBeGreaterThanOrEqualTo(40); // allow timer skew
    }

    [Fact]
    public async Task RunAsync_invokes_onQueryStart_for_each_query_in_order()
    {
        var fake = new FakeRankingSource { Script = _ => Array.Empty<string>() };
        var runner = new EvalRunner(fake);
        var set = Set(
            Query("a", "qa"),
            Query("b", "qb"),
            Query("c", "qc"));

        var progress = new List<(int Index, int Total, string Id)>();
        await runner.RunAsync(set, EvalMode.Keyword, topK: 5, (i, t, id) => progress.Add((i, t, id)));

        progress.ShouldBe(new[] { (0, 3, "a"), (1, 3, "b"), (2, 3, "c") });
    }

    [Fact]
    public async Task RunAsync_returns_a_result_per_query_with_the_configured_mode_and_topK()
    {
        var fake = new FakeRankingSource { Script = _ => new[] { "x@x" } };
        var runner = new EvalRunner(fake);
        var set = Set(Query("a", "qa"), Query("b", "qb"));

        var modeResult = await runner.RunAsync(set, EvalMode.Semantic, topK: 7);

        modeResult.Mode.ShouldBe(EvalMode.Semantic);
        modeResult.TopK.ShouldBe(7);
        modeResult.Queries.Count.ShouldBe(2);
        modeResult.Queries.Select(q => q.Id).ShouldBe(new[] { "a", "b" });
    }

    [Fact]
    public async Task RunAsync_throws_OperationCanceled_when_token_fires_between_queries()
    {
        var fake = new FakeRankingSource { Script = _ => Array.Empty<string>() };
        var runner = new EvalRunner(fake);
        var cts = new CancellationTokenSource();
        var set = Set(
            Query("a", "qa"),
            Query("b", "qb"),
            Query("c", "qc"));

        // Cancel after the first query has been kicked off.
        var progress = new List<string>();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await runner.RunAsync(set, EvalMode.Keyword, topK: 5,
                (i, _, id) =>
                {
                    progress.Add(id);
                    if (i == 0) cts.Cancel();
                },
                cts.Token);
        });

        // First query completed; cancellation prevents queries b and c from starting.
        progress.ShouldBe(new[] { "a" });
        fake.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Passes_query_filters_through_to_the_ranking_source()
    {
        var fake = new FakeRankingSource { Script = _ => Array.Empty<string>() };
        var runner = new EvalRunner(fake);
        var q = new EvalQuery
        {
            Id = "f",
            Query = "with filters",
            Filters = new EvalQueryFilters { Folder = "Archive", FromContains = "alice" },
            Relevant = [new RelevantEntry("x@x")],
        };

        await runner.RunOneAsync(q, EvalMode.Hybrid, topK: 10);

        fake.Calls.Single().Filters.ShouldNotBeNull();
        fake.Calls.Single().Filters!.Folder.ShouldBe("Archive");
        fake.Calls.Single().Filters!.FromContains.ShouldBe("alice");
    }

    [Fact]
    public async Task Filter_free_query_passes_null_filters_through()
    {
        var fake = new FakeRankingSource { Script = _ => Array.Empty<string>() };
        var runner = new EvalRunner(fake);
        var q = Query("q", "no filters", "x@x");

        await runner.RunOneAsync(q, EvalMode.Keyword, topK: 10);

        fake.Calls.Single().Filters.ShouldBeNull();
    }

    [Theory]
    [InlineData(EvalMode.Keyword)]
    [InlineData(EvalMode.Semantic)]
    [InlineData(EvalMode.Hybrid)]
    public async Task Dispatches_each_EvalMode_to_the_ranking_source(EvalMode mode)
    {
        var fake = new FakeRankingSource { Script = _ => Array.Empty<string>() };
        var runner = new EvalRunner(fake);
        var q = Query("q", "x", "expected@x");

        await runner.RunOneAsync(q, mode, topK: 10);

        fake.Calls.Single().Mode.ShouldBe(mode);
        fake.Calls.Single().TopK.ShouldBe(10);
        fake.Calls.Single().Query.ShouldBe("x");
    }

    [Fact]
    public async Task Empty_query_set_returns_an_empty_mode_result()
    {
        var fake = new FakeRankingSource();
        var runner = new EvalRunner(fake);

        var result = await runner.RunAsync(Set(), EvalMode.Hybrid, topK: 10);

        result.Queries.ShouldBeEmpty();
        result.MeanNdcg.ShouldBe(0.0);
        fake.Calls.ShouldBeEmpty();
    }
}
