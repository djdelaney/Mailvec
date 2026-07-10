using Mailvec.Cli.Commands;
using Mailvec.Core.Eval;

namespace Mailvec.Cli.Tests;

/// <summary>
/// The baseline-diff rendering is the instrument the repo's "capture an eval
/// baseline before any ranking change" discipline reads — a bug in its delta
/// math or per-query flip selection silently corrupts the quality signal
/// itself, and nothing downstream would catch it.
/// </summary>
public sealed class EvalCommandBaselineDiffTests
{
    private static string CaptureDiff(IReadOnlyList<EvalModeResult> current, EvalReport baseline, bool includeTiming = false)
    {
        var sw = new StringWriter();
        var prior = Console.Out;
        Console.SetOut(sw);
        try { EvalCommand.PrintBaselineDiff(current, baseline, includeTiming); }
        finally { Console.SetOut(prior); }
        return sw.ToString();
    }

    private static EvalQueryResult Q(string id, double ndcg, bool expectEmpty = false, double latencyMs = 10) => new(
        Id: id, Query: "q", RelevantCount: 1, RankedMessageIds: [], RanksOfExpected: [1],
        Ndcg: ndcg, Mrr: ndcg, Recall: ndcg, LatencyMs: latencyMs,
        ExpectEmpty: expectEmpty, ReturnedCount: expectEmpty ? 0 : 5,
        Specificity: expectEmpty ? 1.0 : double.NaN);

    private static EvalReport BaselineOf(params EvalModeResult[] runs) => new()
    {
        RanAt = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero),
        TopK = 10,
        Runs = runs.Select(EvalReportRun.From).ToList(),
    };

    [Fact]
    public void Diff_reports_aggregate_deltas_and_ranked_per_query_flips()
    {
        var current = new[]
        {
            new EvalModeResult(EvalMode.Hybrid, TopK: 10, Queries:
            [
                Q("q001", 0.5),                    // regressed from 0.9
                Q("q002", 1.0),                    // improved from 0.5
                Q("q003", 0.8),                    // unchanged — under the 0.05 flip threshold
                Q("q900", 0.0, expectEmpty: true), // negative: excluded from NDCG flips
            ]),
        };
        var priorRun = new EvalModeResult(EvalMode.Hybrid, TopK: 10, Queries:
        [
            Q("q001", 0.9), Q("q002", 0.5), Q("q003", 0.8), Q("q900", 0.0, expectEmpty: true),
        ]);
        var baseline = BaselineOf(priorRun);

        var text = CaptureDiff(current, baseline);

        // Aggregate delta row uses scored-only means, formatted through Delta().
        var dN = current[0].MeanNdcg - baseline.Runs[0].Aggregate.Ndcg;
        text.ShouldContain(EvalCommand.Delta(dN));

        // Per-query flips: both movers present with the right prior -> current
        // values and signed deltas, ranked by |delta| (q002's +0.5 outranks
        // q001's -0.4)...
        text.ShouldContain($"{0.5,5:F3} → {1.0,5:F3}"); // same F3/current-culture formatting the renderer uses
        text.ShouldContain("(+0.500)");
        text.ShouldContain($"{0.9,5:F3} → {0.5,5:F3}");
        text.ShouldContain("(-0.400)");
        text.IndexOf("q002", StringComparison.Ordinal).ShouldBeLessThan(
            text.IndexOf("q001", StringComparison.Ordinal));
        // ...with direction arrows either side.
        text.ShouldContain("↑");
        text.ShouldContain("↓");

        // The unchanged query and the negative query don't appear as flips.
        text.ShouldNotContain("q003");
        text.ShouldNotContain("q900");
    }

    [Fact]
    public void Mode_missing_from_the_baseline_reads_as_new()
    {
        var current = new[] { new EvalModeResult(EvalMode.Semantic, TopK: 10, Queries: [Q("q001", 0.7)]) };
        var baseline = BaselineOf(new EvalModeResult(EvalMode.Keyword, TopK: 10, Queries: [Q("q001", 0.7)]));

        var text = CaptureDiff(current, baseline);

        text.ShouldContain("(new)");
    }

    [Fact]
    public void Timing_diff_is_suppressed_for_pre_timing_baselines()
    {
        // Pre-timing baselines carry 0.0 latency fields; diffing against them
        // would print a misleading "speedup". The renderer must say so instead.
        var current = new[] { new EvalModeResult(EvalMode.Hybrid, TopK: 10, Queries: [Q("q001", 0.7, latencyMs: 25)]) };
        var baseline = BaselineOf(new EvalModeResult(EvalMode.Hybrid, TopK: 10, Queries: [Q("q001", 0.7, latencyMs: 0)]));

        var text = CaptureDiff(current, baseline, includeTiming: true);

        text.ShouldContain("baseline has no latency data");
        text.ShouldNotContain("Δp95");
    }
}
