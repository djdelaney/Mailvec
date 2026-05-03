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
        var verboseOpt = new Option<bool>("--verbose", "-v") { Description = "After the aggregate, list per-query metrics for queries with NDCG<1, sorted worst-first, including expected-doc ranks." };
        var allQueriesOpt = new Option<bool>("--all-queries") { Description = "With --verbose, list every query (not just regressions). Useful for sanity-checking which queries are scoring perfectly." };
        var timingOpt = new Option<bool>("--timing") { Description = "Show per-mode mean/p50/p95 search latency in the aggregate (and Δlatency in --baseline diffs). Latency is always recorded in --json reports regardless of this flag." };

        var cmd = new Command("eval", "Run the labeled query set and report search-quality metrics.")
        {
            queriesOpt,
            modeOpt,
            topKOpt,
            queryOpt,
            jsonOpt,
            baselineOpt,
            verboseOpt,
            allQueriesOpt,
            timingOpt,
        };

        cmd.SetAction(async (parseResult, ct) =>
        {
            var path = EvalDefaults.ResolveQuerySetPath(parseResult.GetValue(queriesOpt));
            var modeStr = parseResult.GetValue(modeOpt) ?? "all";
            var topK = parseResult.GetValue(topKOpt);
            var queryId = parseResult.GetValue(queryOpt);
            var jsonPath = parseResult.GetValue(jsonOpt);
            var baselinePath = parseResult.GetValue(baselineOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            var allQueries = parseResult.GetValue(allQueriesOpt);
            var timing = parseResult.GetValue(timingOpt);

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

            if (timing)
            {
                PrintTiming(modeResults);
            }

            if (verbose)
            {
                PrintVerbosePerQuery(modeResults, set, topK, includeAll: allQueries);
            }

            if (baselinePath is not null)
            {
                if (!File.Exists(baselinePath)) { Console.Error.WriteLine($"Baseline {baselinePath} not found."); return 2; }
                var baseline = EvalReport.Load(baselinePath);
                PrintBaselineDiff(modeResults, baseline, includeTiming: timing);
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
        Console.WriteLine(Colors.Dim($"  {new string('-', 10)}  {new string('-', 7)}  {new string('-', 7)}  {new string('-', 7)}"));
        foreach (var r in results)
        {
            Console.WriteLine(
                $"  {Colors.ModeHeader($"{ModeName(r.Mode),-10}")}  " +
                $"{Colors.Score(r.MeanNdcg, $"{r.MeanNdcg,7:F3}")}  " +
                $"{Colors.Score(r.MeanMrr, $"{r.MeanMrr,7:F3}")}  " +
                $"{Colors.Score(r.MeanRecall, $"{r.MeanRecall,7:F3}")}");
        }
    }

    /// <summary>
    /// Per-mode dump of every query that didn't score perfectly (NDCG &lt; 1),
    /// sorted worst-first. For each row, shows the query string, the rank of
    /// each expected message (or "miss" if not in top-k), and a one-line
    /// recap of the actual top-3 so you can see what crowded the expected
    /// docs out. With <paramref name="includeAll"/>, also prints rows that
    /// scored 1.000 (in NDCG-asc order) — useful as a sanity check that the
    /// queries you think are passing actually are.
    /// </summary>
    private static void PrintVerbosePerQuery(IReadOnlyList<EvalModeResult> results, EvalQuerySet set, int topK, bool includeAll)
    {
        var queriesById = set.Queries.ToDictionary(q => q.Id, q => q);

        foreach (var mr in results)
        {
            var rows = mr.Queries
                .Where(q => includeAll || q.Ndcg < 0.9995)
                .OrderBy(q => q.Ndcg)
                .ThenBy(q => q.Mrr)
                .ToList();
            if (rows.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine($"  {ModeName(mr.Mode)}: every query scored NDCG≈1.000 (top-{topK}).");
                continue;
            }

            Console.WriteLine();
            var label = includeAll ? "all queries" : $"{rows.Count} quer{(rows.Count == 1 ? "y" : "ies")} below NDCG=1.000";
            Console.WriteLine($"  {Colors.Bold(Colors.ModeHeader(ModeName(mr.Mode)))}: {label} (worst → best)");
            Console.WriteLine($"    {"Id",-10}  {"NDCG",6}  {"MRR",6}  {"Recall",6}  Query / expected ranks");
            Console.WriteLine(Colors.Dim($"    {new string('-', 10)}  {new string('-', 6)}  {new string('-', 6)}  {new string('-', 6)}  {new string('-', 60)}"));

            var first = true;
            foreach (var q in rows)
            {
                // Blank line between queries — the biggest readability win when
                // many queries each have multi-line expected/actual blocks.
                if (!first) Console.WriteLine();
                first = false;

                var meta = queriesById.GetValueOrDefault(q.Id);
                var queryText = meta is null ? q.Query : meta.Query;
                var trimmed = queryText.Length > 60 ? queryText[..57] + "..." : queryText;
                Console.WriteLine(
                    $"    {Colors.QueryId($"{q.Id,-10}")}  " +
                    $"{Colors.Score(q.Ndcg, $"{q.Ndcg,6:F3}")}  " +
                    $"{Colors.Score(q.Mrr, $"{q.Mrr,6:F3}")}  " +
                    $"{Colors.Score(q.Recall, $"{q.Recall,6:F3}")}  " +
                    $"{trimmed}");

                if (meta is not null)
                {
                    for (var i = 0; i < meta.Relevant.Count; i++)
                    {
                        var rank = q.RanksOfExpected[i];
                        var rankStr = rank == 0 ? $"miss (not in top-{topK})" : $"rank {rank}";
                        var marker = rank == 0 ? Colors.Miss() : Colors.Hit();
                        Console.WriteLine($"      {marker} expected {meta.Relevant[i].MessageId}  →  {rankStr}");
                    }
                }

                // Show actual top-3 so we can see what the search returned instead.
                var relevantSet = meta?.Relevant.Select(x => x.MessageId).ToHashSet() ?? [];
                var actualTop = q.RankedMessageIds.Take(3).ToList();
                if (actualTop.Count > 0)
                {
                    var formatted = actualTop.Select((id, i) =>
                    {
                        var hit = relevantSet.Contains(id);
                        var marker = hit ? Colors.Hit() : " ";
                        return $"{marker}{i + 1}.{id}";
                    });
                    Console.WriteLine($"      {Colors.Dim($"actual top-{actualTop.Count}:")} {string.Join("  ", formatted)}");
                }
            }
        }
    }

    /// <summary>
    /// Per-mode mean / p50 / p95 latency, in milliseconds. Latency is always
    /// captured (cheap Stopwatch); this just prints it on demand. Useful when
    /// scaling the corpus and watching for regressions on the vec0 leg.
    /// </summary>
    private static void PrintTiming(IReadOnlyList<EvalModeResult> results)
    {
        Console.WriteLine();
        Console.WriteLine($"Latency (ms):");
        Console.WriteLine();
        Console.WriteLine($"  {"Mode",-10}  {"mean",8}  {"p50",8}  {"p95",8}");
        Console.WriteLine(Colors.Dim($"  {new string('-', 10)}  {new string('-', 8)}  {new string('-', 8)}  {new string('-', 8)}"));
        foreach (var r in results)
        {
            Console.WriteLine(
                $"  {Colors.ModeHeader($"{ModeName(r.Mode),-10}")}  " +
                $"{r.MeanLatencyMs,8:F1}  {r.P50LatencyMs,8:F1}  {r.P95LatencyMs,8:F1}");
        }
    }

    private static void PrintBaselineDiff(IReadOnlyList<EvalModeResult> current, EvalReport baseline, bool includeTiming)
    {
        Console.WriteLine();
        Console.WriteLine($"Baseline ({baseline.RanAt:u}, top-{baseline.TopK}):");
        Console.WriteLine();
        Console.WriteLine($"  {"Mode",-10}  {"ΔNDCG",8}  {"ΔMRR",8}  {"ΔRecall",8}");
        Console.WriteLine(Colors.Dim($"  {new string('-', 10)}  {new string('-', 8)}  {new string('-', 8)}  {new string('-', 8)}"));
        foreach (var cur in current)
        {
            var prior = baseline.Runs.FirstOrDefault(r => r.Mode == cur.Mode);
            if (prior is null)
            {
                Console.WriteLine($"  {Colors.ModeHeader($"{ModeName(cur.Mode),-10}")}  {"(new)",8}  {"(new)",8}  {"(new)",8}");
                continue;
            }
            var dN = cur.MeanNdcg - prior.Aggregate.Ndcg;
            var dM = cur.MeanMrr - prior.Aggregate.Mrr;
            var dR = cur.MeanRecall - prior.Aggregate.Recall;
            Console.WriteLine(
                $"  {Colors.ModeHeader($"{ModeName(cur.Mode),-10}")}  " +
                $"{Colors.DeltaSigned(dN, $"{Delta(dN),8}")}  " +
                $"{Colors.DeltaSigned(dM, $"{Delta(dM),8}")}  " +
                $"{Colors.DeltaSigned(dR, $"{Delta(dR),8}")}");
        }

        if (includeTiming)
        {
            // Pre-timing baselines store 0.0 for latency fields. Any prior with
            // P95 == 0 is almost certainly missing data, not actually instant —
            // suppress the diff in that case so we don't print misleading speedup.
            var anyComparable = current.Any(c =>
            {
                var p = baseline.Runs.FirstOrDefault(r => r.Mode == c.Mode);
                return p is not null && p.Aggregate.P95LatencyMs > 0;
            });
            if (anyComparable)
            {
                Console.WriteLine();
                Console.WriteLine($"  {"Mode",-10}  {"Δmean",8}  {"Δp50",8}  {"Δp95",8}  {Colors.Dim("(ms; − = faster)")}");
                Console.WriteLine(Colors.Dim($"  {new string('-', 10)}  {new string('-', 8)}  {new string('-', 8)}  {new string('-', 8)}"));
                foreach (var cur in current)
                {
                    var prior = baseline.Runs.FirstOrDefault(r => r.Mode == cur.Mode);
                    if (prior is null || prior.Aggregate.P95LatencyMs == 0)
                    {
                        Console.WriteLine($"  {Colors.ModeHeader($"{ModeName(cur.Mode),-10}")}  {"(no data)",8}  {"(no data)",8}  {"(no data)",8}");
                        continue;
                    }
                    var dMean = cur.MeanLatencyMs - prior.Aggregate.MeanLatencyMs;
                    var dP50 = cur.P50LatencyMs - prior.Aggregate.P50LatencyMs;
                    var dP95 = cur.P95LatencyMs - prior.Aggregate.P95LatencyMs;
                    Console.WriteLine(
                        $"  {Colors.ModeHeader($"{ModeName(cur.Mode),-10}")}  " +
                        $"{Colors.DeltaLatency(dMean, $"{DeltaMs(dMean),8}")}  " +
                        $"{Colors.DeltaLatency(dP50, $"{DeltaMs(dP50),8}")}  " +
                        $"{Colors.DeltaLatency(dP95, $"{DeltaMs(dP95),8}")}");
                }
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine(Colors.Dim("  (baseline has no latency data — re-run baseline with current build to enable Δlatency.)"));
            }
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
            Console.WriteLine($"  {Colors.Bold(Colors.ModeHeader(ModeName(cur.Mode)))}: top per-query NDCG changes (|Δ| ≥ 0.05)");
            foreach (var (q, prev) in rows)
            {
                var d = q.Ndcg - prev!.Ndcg;
                var arrow = d > 0 ? Colors.Up() : Colors.Down();
                Console.WriteLine(
                    $"    {arrow} {Colors.QueryId($"{q.Id,-8}")}  " +
                    $"{Colors.Score(prev.Ndcg, $"{prev.Ndcg,5:F3}")} → {Colors.Score(q.Ndcg, $"{q.Ndcg,5:F3}")}  " +
                    $"({Colors.DeltaSigned(d, d.ToString("+0.000;-0.000", CultureInfo.InvariantCulture))})");
            }
        }
    }

    private static string Delta(double d)
    {
        if (Math.Abs(d) < 0.0005) return "  =0.000";
        return d.ToString("+0.000;-0.000", CultureInfo.InvariantCulture);
    }

    private static string DeltaMs(double d)
    {
        if (Math.Abs(d) < 0.05) return "  =0.0";
        return d.ToString("+0.0;-0.0", CultureInfo.InvariantCulture);
    }

    private static void PrintSingleQuery(EvalQuery q, EvalMode mode, int topK, EvalQueryResult r)
    {
        Console.WriteLine();
        Console.WriteLine($"== {Colors.QueryId(q.Id)}  [{Colors.ModeHeader(ModeName(mode))}]  \"{q.Query}\"");
        if (q.Filters is not null)
            Console.WriteLine($"   {Colors.Dim("filters:")} {FilterSummary(q.Filters)}");
        Console.WriteLine(
            $"   NDCG@{topK}={Colors.Score(r.Ndcg, $"{r.Ndcg:F3}")}  " +
            $"MRR={Colors.Score(r.Mrr, $"{r.Mrr:F3}")}  " +
            $"Recall@{topK}={Colors.Score(r.Recall, $"{r.Recall:F3}")}");
        Console.WriteLine();
        Console.WriteLine($"   Expected ({q.Relevant.Count}):");
        for (var i = 0; i < q.Relevant.Count; i++)
        {
            var expected = q.Relevant[i];
            var rank = r.RanksOfExpected[i];
            var rankStr = rank == 0 ? $"not in top-{topK}" : $"rank {rank}";
            var grade = expected.Grade == 1.0 ? "" : $"  grade={expected.Grade:F1}";
            var marker = rank == 0 ? Colors.Miss() : Colors.Hit();
            Console.WriteLine($"     {marker} {expected.MessageId}  ({rankStr}){grade}");
        }
        Console.WriteLine();
        Console.WriteLine($"   Top {Math.Min(topK, r.RankedMessageIds.Count)}:");
        var relevantSet = q.Relevant.Select(x => x.MessageId).ToHashSet();
        for (var i = 0; i < Math.Min(topK, r.RankedMessageIds.Count); i++)
        {
            var isHit = relevantSet.Contains(r.RankedMessageIds[i]);
            var marker = isHit ? Colors.Hit() : " ";
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

    /// <summary>
    /// ANSI colorization for the eval output. Colors are auto-disabled when
    /// stdout is redirected (so `mailvec eval | tee` stays clean) or when
    /// NO_COLOR is set (https://no-color.org). All helpers wrap an
    /// already-formatted string so column alignment in `{x,-N}` layouts
    /// survives — ANSI codes are zero-width to terminals but counted as
    /// chars by string.Format, so we color AFTER padding.
    /// </summary>
    internal static class Colors
    {
        private static readonly bool Enabled =
            Environment.GetEnvironmentVariable("NO_COLOR") is null
            && (Environment.GetEnvironmentVariable("FORCE_COLOR") is not null
                || !Console.IsOutputRedirected);

        private const string Reset = "\x1b[0m";
        private const string BoldOn = "\x1b[1m";
        private const string DimOn = "\x1b[2m";
        private const string Red = "\x1b[31m";
        private const string Green = "\x1b[32m";
        private const string Yellow = "\x1b[33m";
        private const string Cyan = "\x1b[36m";
        private const string BoldCyan = "\x1b[1;36m";

        private static string Wrap(string s, string codes) => Enabled ? codes + s + Reset : s;

        public static string Bold(string s) => Wrap(s, BoldOn);
        public static string Dim(string s) => Wrap(s, DimOn);
        public static string QueryId(string s) => Wrap(s, BoldCyan);
        public static string Hit(string s = "✓") => Wrap(s, Green);
        public static string Miss(string s = "✗") => Wrap(s, Red);
        public static string Up(string s = "↑") => Wrap(s, Green);
        public static string Down(string s = "↓") => Wrap(s, Red);
        public static string ModeHeader(string s) => Wrap(s, Cyan);

        /// <summary>Color a metric value (NDCG/MRR/Recall) by quality band.</summary>
        public static string Score(double value, string formatted) =>
            Wrap(formatted, value >= 0.9 ? Green : value >= 0.7 ? Yellow : Red);

        /// <summary>Color a delta (improvement/regression) by sign.</summary>
        public static string DeltaSigned(double delta, string formatted)
        {
            if (Math.Abs(delta) < 0.0005) return Wrap(formatted, DimOn);
            return Wrap(formatted, delta > 0 ? Green : Red);
        }

        /// <summary>Color a latency delta — positive (slower) is bad.</summary>
        public static string DeltaLatency(double delta, string formatted)
        {
            if (Math.Abs(delta) < 0.05) return Wrap(formatted, DimOn);
            return Wrap(formatted, delta > 0 ? Red : Green);
        }
    }
}
