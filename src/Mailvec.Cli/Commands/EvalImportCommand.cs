using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mailvec.Core;
using Mailvec.Core.Eval;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Reads recent <c>search_emails</c> tool calls from the MCP Serilog file and
/// drops the user into the <c>eval-add</c> labeling flow with the chosen
/// query/filters pre-filled. Removes the manual transcription step from the
/// "use Claude → notice good search → save as eval query" workflow.
///
/// Requires <c>Mcp:LogToolCalls = true</c> on the MCP server (otherwise
/// <c>mcp-call</c> lines are not emitted and there's nothing to import).
/// </summary>
internal static class EvalImportCommand
{
    public static Command Build()
    {
        var queriesOpt = new Option<string?>("--queries") { Description = $"Path to query set JSON. Default: {EvalDefaults.DefaultQuerySetPath}" };
        var logDirOpt = new Option<string?>("--log-dir") { Description = "Directory containing mailvec-mcp-*.log files. Default: $MAILVEC_LOG_DIR or ~/Library/Logs/Mailvec." };
        var limitOpt = new Option<int>("--limit") { DefaultValueFactory = _ => 20, Description = "Max number of recent unique calls to display." };
        var topKOpt = new Option<int>("--top-k") { DefaultValueFactory = _ => 10, Description = "Number of candidate results to display for labeling (when not pinning)." };
        var pinRelevantOpt = new Option<string[]>("--pin-relevant")
        {
            AllowMultipleArgumentsPerToken = true,
            Description = "Skip the search-and-pick prompt; mark these specific Message-IDs as relevant. Same semantics as `eval-add --pin-relevant`.",
        };
        var notesOpt = new Option<string?>("--notes") { Description = "Free-text note saved alongside the query." };
        var idOpt = new Option<string?>("--id") { Description = "Override the auto-generated query id." };
        var yesOpt = new Option<bool>("--yes", "-y") { Description = "Skip confirmation prompt before saving." };

        var cmd = new Command("eval-import", "Import a recent MCP search_emails call as a labeled eval query.")
        {
            queriesOpt,
            logDirOpt,
            limitOpt,
            topKOpt,
            pinRelevantOpt,
            notesOpt,
            idOpt,
            yesOpt,
        };

        cmd.SetAction(async (parseResult, ct) =>
        {
            var logDir = ResolveLogDir(parseResult.GetValue(logDirOpt));
            if (!Directory.Exists(logDir))
            {
                Console.Error.WriteLine($"Log directory does not exist: {logDir}");
                Console.Error.WriteLine("Set Mcp:LogToolCalls=true on the MCP server, run a search via Claude, and try again.");
                return 2;
            }

            var calls = LoadRecentCalls(logDir, parseResult.GetValue(limitOpt));
            if (calls.Count == 0)
            {
                Console.Error.WriteLine($"No mcp-call lines for search_emails found in {logDir}.");
                Console.Error.WriteLine("Confirm Mcp:LogToolCalls=true on the MCP server, then exercise it via Claude.");
                return 2;
            }

            PrintCallList(calls);

            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("\nstdin is not a TTY; eval-import needs interactive input.");
                return 2;
            }

            Console.Write($"\nPick a call by number (1–{calls.Count}), empty to abort: ");
            var raw = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(raw))
            {
                Console.WriteLine("(aborted)");
                return 0;
            }
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pick) || pick < 1 || pick > calls.Count)
            {
                Console.Error.WriteLine($"Not a valid selection: '{raw}'.");
                return 2;
            }

            var chosen = calls[pick - 1];
            if (string.IsNullOrWhiteSpace(chosen.Args.Query))
            {
                Console.Error.WriteLine("Selected call has no `query` (it was a query-less browse). eval-import only handles ranked queries.");
                return 2;
            }

            var args = new EvalAddFlow.Args(
                Query: chosen.Args.Query!,
                Filters: BuildFilters(chosen.Args),
                Mode: ParseMode(chosen.Args.Mode),
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

    /// <summary>
    /// Mirrors SerilogSetup.ResolveLogDir so eval-import sees logs at the same path
    /// the services write them, with a CLI override for ad-hoc runs.
    /// </summary>
    private static string ResolveLogDir(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return PathExpansion.Expand(overridePath);
        var fromEnv = Environment.GetEnvironmentVariable("MAILVEC_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return PathExpansion.Expand(fromEnv);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Mailvec");
    }

    /// <summary>
    /// Reads <c>mcp-call tool=search_emails</c> entries across every
    /// <c>mailvec-mcp-*.log</c> in <paramref name="logDir"/>, dedupes by
    /// canonical args (Claude often retries with identical params), and
    /// returns the most recent <paramref name="limit"/>.
    /// </summary>
    private static IReadOnlyList<RecentCall> LoadRecentCalls(string logDir, int limit)
    {
        var files = Directory.GetFiles(logDir, "mailvec-mcp-*.log");
        if (files.Length == 0) return [];

        // Regex anchors on the well-known ToolCallLogger output. The args
        // payload is a JSON object — `{...}` capture is greedy to the end of
        // the line, which works because Serilog writes one log event per line.
        var line = new Regex(
            @"^(?<ts>\S+ \S+ \S+) \[INF\] Mailvec\.Mcp\.ToolCallLogger: mcp-call tool=search_emails args=(?<args>\{.*\})$",
            RegexOptions.Compiled);

        var calls = new List<RecentCall>(256);
        foreach (var path in files)
        {
            foreach (var raw in File.ReadLines(path))
            {
                var m = line.Match(raw);
                if (!m.Success) continue;

                if (!DateTimeOffset.TryParse(m.Groups["ts"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts))
                    continue;

                CallArgs? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<CallArgs>(m.Groups["args"].Value, JsonOpts);
                }
                catch (JsonException) { continue; }
                if (parsed is null) continue;

                calls.Add(new RecentCall(ts, parsed, m.Groups["args"].Value));
            }
        }

        // Most-recent first, dedupe by canonical args string, take the requested cap.
        return calls
            .OrderByDescending(c => c.Timestamp)
            .DistinctBy(c => c.RawArgs, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    private static void PrintCallList(IReadOnlyList<RecentCall> calls)
    {
        Console.WriteLine($"Recent search_emails calls ({calls.Count}):\n");
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < calls.Count; i++)
        {
            var c = calls[i];
            var age = HumanizeAge(now - c.Timestamp);
            var query = string.IsNullOrEmpty(c.Args.Query) ? "(browse — no query)" : Truncate(c.Args.Query, 80);
            Console.WriteLine($"  {i + 1,2}.  {age,-9}  {query}");

            var bits = new List<string>();
            if (!string.IsNullOrEmpty(c.Args.Mode) && c.Args.Mode != "hybrid") bits.Add($"mode={c.Args.Mode}");
            if (!string.IsNullOrEmpty(c.Args.Folder)) bits.Add($"folder={c.Args.Folder}");
            if (!string.IsNullOrEmpty(c.Args.DateFrom)) bits.Add($"dateFrom={c.Args.DateFrom}");
            if (!string.IsNullOrEmpty(c.Args.DateTo)) bits.Add($"dateTo={c.Args.DateTo}");
            if (!string.IsNullOrEmpty(c.Args.FromContains)) bits.Add($"fromContains={c.Args.FromContains}");
            if (!string.IsNullOrEmpty(c.Args.FromExact)) bits.Add($"fromExact={c.Args.FromExact}");
            if (bits.Count > 0) Console.WriteLine($"        {string.Join("  ·  ", bits)}");
        }
    }

    private static string HumanizeAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60) return $"{(int)age.TotalSeconds}s ago";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 48) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    private static EvalQueryFilters? BuildFilters(CallArgs args)
    {
        if (string.IsNullOrEmpty(args.Folder) &&
            string.IsNullOrEmpty(args.DateFrom) &&
            string.IsNullOrEmpty(args.DateTo) &&
            string.IsNullOrEmpty(args.FromContains) &&
            string.IsNullOrEmpty(args.FromExact))
            return null;

        return new EvalQueryFilters
        {
            Folder = NullIfEmpty(args.Folder),
            FromContains = NullIfEmpty(args.FromContains),
            FromExact = NullIfEmpty(args.FromExact),
            DateFrom = ParseDateTo(args.DateFrom),
            DateTo = ParseDateTo(args.DateTo),
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static DateTimeOffset? ParseDateTo(string? s) =>
        string.IsNullOrEmpty(s)
            ? null
            : DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
                ? dto
                : null;

    private static EvalMode ParseMode(string? s) => (s ?? "hybrid").ToLowerInvariant() switch
    {
        "keyword" or "fts" => EvalMode.Keyword,
        "semantic" or "vector" => EvalMode.Semantic,
        _ => EvalMode.Hybrid,
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record RecentCall(DateTimeOffset Timestamp, CallArgs Args, string RawArgs);

    /// <summary>Mirror of the search_emails MCP tool's parameter set.</summary>
    private sealed class CallArgs
    {
        public string? Query { get; set; }
        public string? Mode { get; set; }
        public int? Limit { get; set; }
        public string? Folder { get; set; }
        public string? DateFrom { get; set; }
        public string? DateTo { get; set; }
        public string? FromContains { get; set; }
        public string? FromExact { get; set; }
    }
}
