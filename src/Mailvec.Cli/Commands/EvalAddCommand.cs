using System.CommandLine;
using System.Globalization;
using Mailvec.Core.Eval;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Thin CLI wrapper around <see cref="EvalAddFlow.RunAsync"/>. The actual
/// labeling/saving logic lives there so <c>eval-import</c> can reuse it.
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
        var pinRelevantOpt = new Option<string[]>("--pin-relevant")
        {
            AllowMultipleArgumentsPerToken = true,
            Description = "Skip the search-and-pick prompt; mark these specific Message-IDs as relevant. Repeat the flag or pass space-separated IDs. Use `<id>=<grade>` for graded relevance. IDs are validated against the archive — typos error out instead of silently saving 0-recall queries.",
        };
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
            pinRelevantOpt,
            yesOpt,
        };

        cmd.SetAction(async (parseResult, ct) =>
        {
            var modeStr = parseResult.GetValue(modeOpt) ?? "hybrid";
            var mode = ParseMode(modeStr);
            if (mode is null) { Console.Error.WriteLine($"Unknown --mode '{modeStr}'. Use keyword|semantic|hybrid."); return 2; }

            EvalQueryFilters? filters;
            try
            {
                filters = BuildFilters(
                    parseResult.GetValue(folderOpt),
                    parseResult.GetValue(fromContainsOpt),
                    parseResult.GetValue(fromExactOpt),
                    parseResult.GetValue(dateFromOpt),
                    parseResult.GetValue(dateToOpt));
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }

            var args = new EvalAddFlow.Args(
                Query: parseResult.GetValue(queryArg)!,
                Filters: filters,
                Mode: mode.Value,
                TopK: parseResult.GetValue(topKOpt),
                IdOverride: parseResult.GetValue(idOpt),
                Notes: parseResult.GetValue(notesOpt),
                PinRelevantIds: parseResult.GetValue(pinRelevantOpt),
                Yes: parseResult.GetValue(yesOpt),
                QuerySetPath: EvalDefaults.ResolveQuerySetPath(parseResult.GetValue(queriesOpt)));

            using var sp = CliServices.Build();
            return await EvalAddFlow.RunAsync(sp, args, ct);
        });

        return cmd;
    }

    public static EvalMode? ParseMode(string s) => s.ToLowerInvariant() switch
    {
        "keyword" or "k" or "fts" => EvalMode.Keyword,
        "semantic" or "vector" or "v" => EvalMode.Semantic,
        "hybrid" or "h" => EvalMode.Hybrid,
        _ => null,
    };

    public static EvalQueryFilters? BuildFilters(string? folder, string? fromContains, string? fromExact, string? dateFrom, string? dateTo)
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
}
