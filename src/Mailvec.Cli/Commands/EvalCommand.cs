using System.CommandLine;
using System.Globalization;
using Mailvec.Core.Data;
using Mailvec.Core.Eval;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Commands;

internal static class EvalCommand
{
    public static Command Build()
    {
        var queriesOpt = new Option<string?>("--queries") { Description = $"Path to query set JSON. Default: {EvalDefaults.DefaultQuerySetPath}" };
        var modeOpt = new Option<string>("--mode") { DefaultValueFactory = _ => "all", Description = "keyword | semantic | hybrid | all" };
        var topKOpt = new Option<int>("--top-k") { DefaultValueFactory = _ => 10, Description = "Cutoff for NDCG@k / MRR@k / Recall@k." };
        var queryOpt = new Option<string?>("--query") { Description = "Run a single query by id; prints ranks of expected docs and the top-k list." };
        var jsonOpt = new Option<string?>("--json") { Description = "Write the run as a JSON report at this path." };
        var baselineOpt = new Option<string?>("--baseline") { Description = "Path to a previous --json report; diff aggregate + per-query metrics against it." };

        var cmd = new Command("eval", "Run the labeled query set and report search-quality metrics.")
        {
            queriesOpt,
            modeOpt,
            topKOpt,
            queryOpt,
            jsonOpt,
            baselineOpt,
        };

        cmd.SetAction(async (parseResult, ct) =>
        {
            var path = EvalDefaults.ResolveQuerySetPath(parseResult.GetValue(queriesOpt));
            var modeStr = parseResult.GetValue(modeOpt) ?? "all";
            var topK = parseResult.GetValue(topKOpt);
            var queryId = parseResult.GetValue(queryOpt);
            var jsonPath = parseResult.GetValue(jsonOpt);
            var baselinePath = parseResult.GetValue(baselineOpt);

            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"No query set at {path}. Create one with `mailvec eval-add \"...\"`.");
                return 2;
            }

            var modes = ParseModes(modeStr);
            if (modes is null) { Console.Error.WriteLine($"Unknown --mode '{modeStr}'. Use keyword|semantic|hybrid|all."); return 2; }

            var set = EvalQuerySet.Load(path);
            if (set.Queries.Count == 0)
            {
                Console.Error.WriteLine($"Query set at {path} is empty.");
                return 2;
            }

            using var sp = CliServices.Build();
            sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
            var runner = ActivatorUtilities.CreateInstance<EvalRunner>(sp);

            if (queryId is not null)
            {
                var q = set.Queries.FirstOrDefault(x => x.Id == queryId);
                if (q is null) { Console.Error.WriteLine($"No query with id '{queryId}' in {path}."); return 2; }
                foreach (var mode in modes)
                {
                    var r = await runner.RunOneAsync(q, mode, topK, ct);
                    PrintSingleQuery(q, mode, topK, r);
                }
                return 0;
            }

            var modeResults = new List<EvalModeResult>(modes.Count);
            foreach (var mode in modes)
            {
                var result = await runner.RunAsync(set, mode, topK, ct);
                modeResults.Add(result);
            }

            PrintAggregate(modeResults, topK, set.Queries.Count);

            if (baselinePath is not null)
            {
                if (!File.Exists(baselinePath)) { Console.Error.WriteLine($"Baseline {baselinePath} not found."); return 2; }
                var baseline = EvalReport.Load(baselinePath);
                PrintBaselineDiff(modeResults, baseline);
            }

            if (jsonPath is not null)
            {
                EvalReport.From(modeResults, querySetPath: path, topK: topK).Save(jsonPath);
                Console.WriteLine($"\nWrote report → {jsonPath}");
            }

            return 0;
        });

        return cmd;
    }

    private static IReadOnlyList<EvalMode>? ParseModes(string s) => s.ToLowerInvariant() switch
    {
        "all" => [EvalMode.Keyword, EvalMode.Semantic, EvalMode.Hybrid],
        "keyword" or "k" or "fts" => [EvalMode.Keyword],
        "semantic" or "vector" or "v" => [EvalMode.Semantic],
        "hybrid" or "h" => [EvalMode.Hybrid],
        _ => null,
    };

    private static void PrintAggregate(IReadOnlyList<EvalModeResult> results, int topK, int queryCount)
    {
        Console.WriteLine($"Eval over {queryCount} quer{(queryCount == 1 ? "y" : "ies")} (top-{topK}):");
        Console.WriteLine();
        Console.WriteLine($"  {"Mode",-10}  {"NDCG",7}  {"MRR",7}  {"Recall",7}");
        Console.WriteLine($"  {new string('-', 10)}  {new string('-', 7)}  {new string('-', 7)}  {new string('-', 7)}");
        foreach (var r in results)
        {
            Console.WriteLine($"  {ModeName(r.Mode),-10}  {r.MeanNdcg,7:F3}  {r.MeanMrr,7:F3}  {r.MeanRecall,7:F3}");
        }
    }

    private static void PrintBaselineDiff(IReadOnlyList<EvalModeResult> current, EvalReport baseline)
    {
        Console.WriteLine();
        Console.WriteLine($"Baseline ({baseline.RanAt:u}, top-{baseline.TopK}):");
        Console.WriteLine();
        Console.WriteLine($"  {"Mode",-10}  {"ΔNDCG",8}  {"ΔMRR",8}  {"ΔRecall",8}");
        Console.WriteLine($"  {new string('-', 10)}  {new string('-', 8)}  {new string('-', 8)}  {new string('-', 8)}");
        foreach (var cur in current)
        {
            var prior = baseline.Runs.FirstOrDefault(r => r.Mode == cur.Mode);
            if (prior is null)
            {
                Console.WriteLine($"  {ModeName(cur.Mode),-10}  {"(new)",8}  {"(new)",8}  {"(new)",8}");
                continue;
            }
            Console.WriteLine($"  {ModeName(cur.Mode),-10}  {Delta(cur.MeanNdcg - prior.Aggregate.Ndcg),8}  {Delta(cur.MeanMrr - prior.Aggregate.Mrr),8}  {Delta(cur.MeanRecall - prior.Aggregate.Recall),8}");
        }

        // Per-query NDCG flips, sorted by largest absolute change.
        Console.WriteLine();
        foreach (var cur in current)
        {
            var prior = baseline.Runs.FirstOrDefault(r => r.Mode == cur.Mode);
            if (prior is null) continue;
            var priorById = prior.Queries.ToDictionary(q => q.Id, q => q);
            var rows = cur.Queries
                .Select(q => (q, prev: priorById.GetValueOrDefault(q.Id)))
                .Where(t => t.prev is not null && Math.Abs(t.q.Ndcg - t.prev!.Ndcg) >= 0.05)
                .OrderByDescending(t => Math.Abs(t.q.Ndcg - t.prev!.Ndcg))
                .Take(10)
                .ToList();
            if (rows.Count == 0) continue;
            Console.WriteLine($"  {ModeName(cur.Mode)}: top per-query NDCG changes (|Δ| ≥ 0.05)");
            foreach (var (q, prev) in rows)
            {
                var d = q.Ndcg - prev!.Ndcg;
                var arrow = d > 0 ? "↑" : "↓";
                Console.WriteLine($"    {arrow} {q.Id,-8}  {prev.Ndcg,5:F3} → {q.Ndcg,5:F3}  ({d:+0.000;-0.000})");
            }
        }
    }

    private static string Delta(double d)
    {
        if (Math.Abs(d) < 0.0005) return "  =0.000";
        return d.ToString("+0.000;-0.000", CultureInfo.InvariantCulture);
    }

    private static void PrintSingleQuery(EvalQuery q, EvalMode mode, int topK, EvalQueryResult r)
    {
        Console.WriteLine();
        Console.WriteLine($"== {q.Id}  [{ModeName(mode)}]  \"{q.Query}\"");
        if (q.Filters is not null)
            Console.WriteLine($"   filters: {FilterSummary(q.Filters)}");
        Console.WriteLine($"   NDCG@{topK}={r.Ndcg:F3}  MRR={r.Mrr:F3}  Recall@{topK}={r.Recall:F3}");
        Console.WriteLine();
        Console.WriteLine($"   Expected ({q.Relevant.Count}):");
        for (var i = 0; i < q.Relevant.Count; i++)
        {
            var expected = q.Relevant[i];
            var rank = r.RanksOfExpected[i];
            var rankStr = rank == 0 ? $"not in top-{topK}" : $"rank {rank}";
            var grade = expected.Grade == 1.0 ? "" : $"  grade={expected.Grade:F1}";
            Console.WriteLine($"     {(rank == 0 ? "✗" : "✓")} {expected.MessageId}  ({rankStr}){grade}");
        }
        Console.WriteLine();
        Console.WriteLine($"   Top {Math.Min(topK, r.RankedMessageIds.Count)}:");
        var relevantSet = q.Relevant.Select(x => x.MessageId).ToHashSet();
        for (var i = 0; i < Math.Min(topK, r.RankedMessageIds.Count); i++)
        {
            var marker = relevantSet.Contains(r.RankedMessageIds[i]) ? "✓" : " ";
            Console.WriteLine($"     {marker} {i + 1,2}. {r.RankedMessageIds[i]}");
        }
    }

    private static string FilterSummary(EvalQueryFilters f)
    {
        var parts = new List<string>();
        if (f.Folder is not null) parts.Add($"folder={f.Folder}");
        if (f.DateFrom is not null) parts.Add($"dateFrom={f.DateFrom:yyyy-MM-dd}");
        if (f.DateTo is not null) parts.Add($"dateTo={f.DateTo:yyyy-MM-dd}");
        if (f.FromContains is not null) parts.Add($"fromContains={f.FromContains}");
        if (f.FromExact is not null) parts.Add($"fromExact={f.FromExact}");
        return string.Join(", ", parts);
    }

    private static string ModeName(EvalMode m) => m switch
    {
        EvalMode.Keyword => "keyword",
        EvalMode.Semantic => "semantic",
        EvalMode.Hybrid => "hybrid",
        _ => m.ToString().ToLowerInvariant(),
    };
}
