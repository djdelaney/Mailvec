using System.Globalization;
using Mailvec.Core.Eval;
using Mailvec.Core.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Candidate gathering, display, prompt, and summary helpers shared by
/// EvalAddFlow's interactive path. Lifted out so EvalAddFlow can be small
/// and so any future caller (eval-import lands here too) doesn't have to
/// reimplement the labeling UI.
/// </summary>
internal static class EvalAddCandidates
{
    public sealed record Pick(int Rank, double Grade);

    public static async Task<IReadOnlyList<EvalAddCandidate>> GatherAsync(
        IServiceProvider sp, string query, EvalMode mode, int topK, SearchFilters? filters, CancellationToken ct)
    {
        switch (mode)
        {
            case EvalMode.Keyword:
            {
                var hits = sp.GetRequiredService<KeywordSearchService>().Search(query, topK, filters);
                return hits.Select(h => new EvalAddCandidate(h.MessageIdHeader, h.Subject, h.FromName ?? h.FromAddress, h.DateSent, h.Folder, h.Snippet)).ToList();
            }
            case EvalMode.Semantic:
            {
                // Mirror HybridSearchService's k-inflation when filters are present:
                // vec0 KNN runs before the filter join, so a small k + restrictive
                // filter can return an empty post-filter set.
                var k = (filters is null || filters.IsEmpty) ? Math.Max(100, topK * 5) : Math.Max(500, topK * 50);
                var hits = await sp.GetRequiredService<VectorSearchService>().SearchAsync(query, topK, k: k, filters, ct);
                return hits.Select(h => new EvalAddCandidate(h.MessageIdHeader, h.Subject, h.FromName ?? h.FromAddress, h.DateSent, h.Folder, Truncate(h.ChunkText, 240))).ToList();
            }
            case EvalMode.Hybrid:
            {
                var hits = await sp.GetRequiredService<HybridSearchService>().SearchAsync(query, topK, filters: filters, ct: ct);
                return hits.Select(h => new EvalAddCandidate(h.MessageIdHeader, h.Subject, h.FromName ?? h.FromAddress, h.DateSent, h.Folder, h.Snippet)).ToList();
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    public static void PrintCandidates(IReadOnlyList<EvalAddCandidate> candidates)
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

    public static IReadOnlyList<Pick> PromptForPicks(int candidateCount)
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
        foreach (var token in line.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries))
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

    public static void PrintSummary(EvalQuery q, IReadOnlyList<EvalAddCandidate> candidates)
    {
        Console.WriteLine();
        Console.WriteLine($"Will save query '{q.Id}':");
        Console.WriteLine($"  query: {q.Query}");
        if (q.Filters is not null)
        {
            Console.WriteLine($"  filters: {FilterSummary(q.Filters)}");
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
    private static string Bold(string s) => UseColor ? $"\x1b[1m{s}\x1b[0m" : s;
    private static string Dim(string s) => UseColor ? $"\x1b[2m{s}\x1b[0m" : s;
}
