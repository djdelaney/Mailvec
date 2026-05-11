using Mailvec.Core.Eval;

namespace Mailvec.Core.Tests.Eval;

/// <summary>
/// JSON round-trip coverage for <see cref="EvalReport"/> and friends.
/// Stays self-contained — no DB or search services, just record construction
/// and (de)serialization. Exercises <c>From</c>, <c>Save</c>, <c>Load</c>,
/// and the aggregation fan-out into <c>EvalReportRun</c>.
/// </summary>
public sealed class EvalReportTests : IDisposable
{
    private readonly string _tempRoot;

    public EvalReportTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mailvec-eval-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private static EvalQueryResult Q(string id, double ndcg, double latency, params int[] ranksOfExpected) =>
        new(
            Id: id,
            Query: "q",
            RelevantCount: ranksOfExpected.Length,
            RankedMessageIds: Array.Empty<string>(),
            RanksOfExpected: ranksOfExpected,
            Ndcg: ndcg,
            Mrr: ndcg,
            Recall: ndcg,
            LatencyMs: latency);

    [Fact]
    public void From_populates_top_level_fields_and_per_mode_runs()
    {
        var modeResults = new[]
        {
            new EvalModeResult(EvalMode.Keyword, 10, new[]
            {
                Q("q1", ndcg: 1.0, latency: 5, 1),
                Q("q2", ndcg: 0.5, latency: 15, 0),
            }),
            new EvalModeResult(EvalMode.Hybrid, 10, new[]
            {
                Q("q1", ndcg: 0.8, latency: 25, 1, 3),
            }),
        };

        var report = EvalReport.From(modeResults, querySetPath: "/tmp/q.json", topK: 10);

        report.Version.ShouldBe(1);
        report.QuerySetPath.ShouldBe("/tmp/q.json");
        report.TopK.ShouldBe(10);
        report.Runs.Count.ShouldBe(2);

        var keyword = report.Runs[0];
        keyword.Mode.ShouldBe(EvalMode.Keyword);
        keyword.Aggregate.QueryCount.ShouldBe(2);
        keyword.Aggregate.Ndcg.ShouldBe(0.75, tolerance: 1e-9);
        keyword.Aggregate.MeanLatencyMs.ShouldBe(10.0, tolerance: 1e-9);
        keyword.Queries.Count.ShouldBe(2);
        keyword.Queries[0].Id.ShouldBe("q1");
        keyword.Queries[0].RanksOfExpected.ShouldBe(new[] { 1 });

        var hybrid = report.Runs[1];
        hybrid.Mode.ShouldBe(EvalMode.Hybrid);
        hybrid.Queries[0].RanksOfExpected.ShouldBe(new[] { 1, 3 });
    }

    [Fact]
    public void Save_then_Load_round_trips_all_fields()
    {
        var original = EvalReport.From(new[]
        {
            new EvalModeResult(EvalMode.Semantic, 10, new[]
            {
                Q("alpha", ndcg: 0.42, latency: 12.5, 2),
            }),
        }, querySetPath: "/queries.json", topK: 10);

        var path = Path.Combine(_tempRoot, "nested", "out.json");
        original.Save(path);
        File.Exists(path).ShouldBeTrue();

        var loaded = EvalReport.Load(path);

        loaded.Version.ShouldBe(original.Version);
        loaded.QuerySetPath.ShouldBe(original.QuerySetPath);
        loaded.TopK.ShouldBe(original.TopK);
        loaded.Runs.Count.ShouldBe(1);
        loaded.Runs[0].Mode.ShouldBe(EvalMode.Semantic);
        loaded.Runs[0].Aggregate.Ndcg.ShouldBe(0.42, tolerance: 1e-9);
        loaded.Runs[0].Queries[0].Id.ShouldBe("alpha");
        loaded.Runs[0].Queries[0].RanksOfExpected.ShouldBe(new[] { 2 });
    }

    [Fact]
    public void Save_uses_camelCase_property_names()
    {
        var report = EvalReport.From(new[]
        {
            new EvalModeResult(EvalMode.Hybrid, 5, new[] { Q("q1", ndcg: 1.0, latency: 1) }),
        }, querySetPath: null, topK: 5);

        var path = Path.Combine(_tempRoot, "out.json");
        report.Save(path);
        var text = File.ReadAllText(path);

        text.ShouldContain("\"version\":");
        text.ShouldContain("\"ranAt\":");
        text.ShouldContain("\"topK\":");
        text.ShouldContain("\"runs\":");
        // Enum is camelCase too (configured via JsonStringEnumConverter).
        text.ShouldContain("\"hybrid\"");
        // Null QuerySetPath is omitted (DefaultIgnoreCondition.WhenWritingNull).
        text.ShouldNotContain("querySetPath");
    }

    [Fact]
    public void Load_throws_for_an_empty_file()
    {
        var path = Path.Combine(_tempRoot, "empty.json");
        File.WriteAllText(path, "null");

        Should.Throw<InvalidDataException>(() => EvalReport.Load(path));
    }
}
