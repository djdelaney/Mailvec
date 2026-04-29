using System.CommandLine;
using System.Globalization;
using Mailvec.Core.Data;
using Mailvec.Core.Eval;
using Mailvec.Core.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Interactive bootstrapper: runs a candidate query, lets the user mark
/// relevant results by rank number, and appends a labeled query to the
/// eval set. Designed to lower the labeling-friction tax — adding a
/// query while the search is in front of you is much cheaper than
/// curating IDs by hand.
/// </summary>
internal static class EvalAddCommand
{
    public static Command Build()
    {
        var queryArg = new Argument<string>("query") { Description = "Natural-language query to label." };
        var queriesOpt = new Option<string?>("--queries") { Description = $"Path to query set JSON. Default: {EvalDefaults.DefaultQuerySetPath}" };
        var modeOpt = new Option<string>("--mode") { DefaultValueFactory = _ => "hybrid", Description = "keyword | semantic | hybrid" };
        var topKOpt = new Option<int>("--top-k") { DefaultValueFactory = _ => 10, Description = "Number of candidate results to display for labeling." };
        var idOpt = new Option<string?>("--id") { Description = "Override the auto-generated query id (default: q###)." };
        var notesOpt = new Option<string?>("--notes") { Description = "Free-text note saved alongside the query." };
        var folderOpt = new Option<string?>("--folder") { Description = "SearchFilters.Folder (exact)." };
        var fromContainsOpt = new Option<string?>("--from-contains") { Description = "SearchFilters.FromContains (substring, case-insensitive)." };
        var fromExactOpt = new Option<string?>("--from-exact") { Description = "SearchFilters.FromExact (case-insensitive on from_address)." };
        var dateFromOpt = new Option<string?>("--date-from") { Description = "SearchFilters.DateFrom (ISO 8601 or yyyy-MM-dd)." };
        var dateToOpt = new Option<string?>("--date-to") { Description = "SearchFilters.DateTo (ISO 8601 or yyyy-MM-dd)." };
        var yesOpt = new Option<bool>("--yes", "-y") { Description = "Skip confirmation prompt before saving." };

        var cmd = new Command("eval-add", "Run a query, label relevant results interactively, and append to the eval set.")
        {
            queryArg,
            queriesOpt,
            modeOpt,
            topKOpt,
            idOpt,
            notesOpt,
            folderOpt,
            fromContainsOpt,
            fromExactOpt,
            dateFromOpt,
            dateToOpt,
            yesOpt,
        };

        cmd.SetAction(async (parseResult, ct) =>
        {
            var query = parseResult.GetValue(queryArg)!;
            var path = EvalDefaults.ResolveQuerySetPath(parseResult.GetValue(queriesOpt));
            var modeStr = parseResult.GetValue(modeOpt) ?? "hybrid";
            var topK = parseResult.GetValue(topKOpt);
            var idOverride = parseResult.GetValue(idOpt);
            var notes = parseResult.GetValue(notesOpt);

            var filters = BuildFilters(
                parseResult.GetValue(folderOpt),
                parseResult.GetValue(fromContainsOpt),
                parseResult.GetValue(fromExactOpt),
                parseResult.GetValue(dateFromOpt),
                parseResult.GetValue(dateToOpt));

            var mode = ParseMode(modeStr);
            if (mode is null) { Console.Error.WriteLine($"Unknown --mode '{modeStr}'. Use keyword|semantic|hybrid."); return 2; }

            using var sp = CliServices.Build();
            sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();

            var candidates = await GatherCandidatesAsync(sp, query, mode.Value, topK, filters?.ToSearchFilters(), ct);
            if (candidates.Count == 0)
            {
                Console.WriteLine("(no matches — nothing to label)");
                return 1;
            }

            PrintCandidates(candidates);

            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("\nstdin is not a TTY; eval-add needs interactive input.");
                return 2;
            }

            var picks = PromptForPicks(candidates.Count);
            if (picks.Count == 0)
            {
                Console.WriteLine("(no picks — nothing saved)");
                return 0;
            }

            var relevant = picks
                .Select(p => new RelevantEntry(candidates[p.Rank - 1].MessageIdHeader, p.Grade))
                .ToList();

            var set = EvalQuerySet.LoadOrEmpty(path);
            var id = idOverride ?? set.NextSequentialId();
            if (set.Queries.Any(q => q.Id == id))
            {
                Console.Error.WriteLine($"Id '{id}' already exists in {path}.");
                return 2;
            }

            var newQuery = new EvalQuery
            {
                Id = id,
                Query = query,
                Filters = filters,
                Relevant = relevant,
                Notes = notes,
            };

            PrintSummary(newQuery, candidates);

            if (!parseResult.GetValue(yesOpt))
            {
                Console.Write("Save? [y/N]: ");
                var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (confirm != "y" && confirm != "yes")
                {
                    Console.WriteLine("(not saved)");
                    return 0;
                }
            }

            set.Queries.Add(newQuery);
            set.Save(path);
            Console.WriteLine($"Saved {id} → {path}  (set now has {set.Queries.Count} quer{(set.Queries.Count == 1 ? "y" : "ies")})");
            return 0;
        });

        return cmd;
    }

    private static EvalMode? ParseMode(string s) => s.ToLowerInvariant() switch
    {
        "keyword" or "k" or "fts" => EvalMode.Keyword,
        "semantic" or "vector" or "v" => EvalMode.Semantic,
        "hybrid" or "h" => EvalMode.Hybrid,
        _ => null,
    };

    private static EvalQueryFilters? BuildFilters(string? folder, string? fromContains, string? fromExact, string? dateFrom, string? dateTo)
    {
        if (folder is null && fromContains is null && fromExact is null && dateFrom is null && dateTo is null)
            return null;

        return new EvalQueryFilters
        {
            Folder = folder,
            FromContains = fromContains,
            FromExact = fromExact,
            DateFrom = ParseDate(dateFrom, "--date-from"),
            DateTo = ParseDate(dateTo, "--date-to"),
        };
    }

    private static DateTimeOffset? ParseDate(string? s, string flag)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return dto;
        throw new ArgumentException($"Could not parse {flag}='{s}' as a date.");
    }

    private sealed record Candidate(string MessageIdHeader, string? Subject, string? From, DateTimeOffset? Date, string Folder, string Snippet);

    private static async Task<IReadOnlyList<Candidate>> GatherCandidatesAsync(IServiceProvider sp, string query, EvalMode mode, int topK, SearchFilters? filters, CancellationToken ct)
    {
        switch (mode)
        {
            case EvalMode.Keyword:
            {
                var hits = sp.GetRequiredService<KeywordSearchService>().Search(query, topK, filters);
                return hits.Select(h => new Candidate(h.MessageIdHeader, h.Subject, h.FromName ?? h.FromAddress, h.DateSent, h.Folder, h.Snippet)).ToList();
            }
            case EvalMode.Semantic:
            {
                var k = (filters is null || filters.IsEmpty) ? Math.Max(100, topK * 5) : Math.Max(500, topK * 50);
                var hits = await sp.GetRequiredService<VectorSearchService>().SearchAsync(query, topK, k: k, filters, ct);
                return hits.Select(h => new Candidate(h.MessageIdHeader, h.Subject, h.FromName ?? h.FromAddress, h.DateSent, h.Folder, Truncate(h.ChunkText, 240))).ToList();
            }
            case EvalMode.Hybrid:
            {
                var hits = await sp.GetRequiredService<HybridSearchService>().SearchAsync(query, topK, filters: filters, ct: ct);
                return hits.Select(h => new Candidate(h.MessageIdHeader, h.Subject, h.FromName ?? h.FromAddress, h.DateSent, h.Folder, h.Snippet)).ToList();
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static void PrintCandidates(IReadOnlyList<Candidate> candidates)
    {
        Console.WriteLine();
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var date = c.Date?.ToString("yyyy-MM-dd") ?? "          ";
            var rank = Bold($"{i + 1,2}.");
            Console.WriteLine($"  {rank}  {Bold(c.Subject ?? "(no subject)")}");
            Console.WriteLine($"       {Dim($"{date}  {c.Folder}  ·  {c.From ?? "(unknown)"}")}");
            if (!string.IsNullOrEmpty(c.Snippet)) Console.WriteLine($"       {Dim(Truncate(CollapseWhitespace(c.Snippet), 120))}");
            Console.WriteLine($"       {Dim(c.MessageIdHeader)}");
            Console.WriteLine();
        }
    }

    private sealed record Pick(int Rank, double Grade);

    private static IReadOnlyList<Pick> PromptForPicks(int candidateCount)
    {
        Console.WriteLine($"Enter the {Bold("rank numbers")} (the {Bold("1.")}, {Bold("2.")}, … on the left) of the relevant results,");
        Console.WriteLine($"separated by spaces. Example: {Bold("1 3 5")} marks results 1, 3, and 5 as relevant.");
        Console.WriteLine($"For graded relevance use {Bold("N=G")} (e.g. {Bold("2=3")} marks rank 2 with grade 3).");
        Console.WriteLine("Empty line to abort. (The Message-IDs of your picks are what gets saved.)");
        Console.Write("> ");
        var line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line)) return [];

        var picks = new List<Pick>();
        var seen = new HashSet<int>();
        foreach (var token in line.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int rank;
            double grade = 1.0;
            var eq = token.IndexOf('=');
            if (eq < 0)
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out rank))
                { Console.Error.WriteLine($"Skipping unparseable token '{token}'."); continue; }
            }
            else
            {
                if (!int.TryParse(token.AsSpan(0, eq), NumberStyles.Integer, CultureInfo.InvariantCulture, out rank))
                { Console.Error.WriteLine($"Skipping unparseable token '{token}'."); continue; }
                if (!double.TryParse(token.AsSpan(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out grade))
                { Console.Error.WriteLine($"Skipping unparseable token '{token}'."); continue; }
            }

            if (rank < 1 || rank > candidateCount)
            { Console.Error.WriteLine($"Skipping out-of-range rank {rank} (have {candidateCount} candidates)."); continue; }
            if (!seen.Add(rank))
            { Console.Error.WriteLine($"Skipping duplicate rank {rank}."); continue; }

            picks.Add(new Pick(rank, grade));
        }
        return picks;
    }

    private static void PrintSummary(EvalQuery q, IReadOnlyList<Candidate> candidates)
    {
        Console.WriteLine();
        Console.WriteLine($"Will save query '{q.Id}':");
        Console.WriteLine($"  query: {q.Query}");
        if (q.Filters is not null)
        {
            Console.WriteLine($"  filters: {EvalCommand_FilterSummary(q.Filters)}");
        }
        if (q.Notes is not null) Console.WriteLine($"  notes: {q.Notes}");
        Console.WriteLine($"  relevant ({q.Relevant.Count}):");
        foreach (var r in q.Relevant)
        {
            var c = candidates.FirstOrDefault(x => x.MessageIdHeader == r.MessageId);
            var grade = r.Grade == 1.0 ? "" : $"  grade={r.Grade:F1}";
            Console.WriteLine($"    - {r.MessageId}{grade}");
            if (c is not null) Console.WriteLine($"      {c.Subject ?? "(no subject)"}");
        }
        Console.WriteLine();
    }

    private static string EvalCommand_FilterSummary(EvalQueryFilters f)
    {
        var parts = new List<string>();
        if (f.Folder is not null) parts.Add($"folder={f.Folder}");
        if (f.DateFrom is not null) parts.Add($"dateFrom={f.DateFrom:yyyy-MM-dd}");
        if (f.DateTo is not null) parts.Add($"dateTo={f.DateTo:yyyy-MM-dd}");
        if (f.FromContains is not null) parts.Add($"fromContains={f.FromContains}");
        if (f.FromExact is not null) parts.Add($"fromExact={f.FromExact}");
        return string.Join(", ", parts);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string CollapseWhitespace(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var prevWasSpace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevWasSpace && sb.Length > 0) sb.Append(' ');
                prevWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                prevWasSpace = false;
            }
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }

    private static readonly bool UseColor = !Console.IsOutputRedirected;
    private static string Bold(string s) => UseColor ? $"[1m{s}[0m" : s;
    private static string Dim(string s) => UseColor ? $"[2m{s}[0m" : s;
}
