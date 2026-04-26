using System.CommandLine;
using Mailvec.Core.Data;
using Mailvec.Core.Search;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Commands;

internal static class SearchCommand
{
    public static Command Build()
    {
        var queryArg = new Argument<string>("query") { Description = "FTS5 query string." };
        var limitOpt = new Option<int>("--limit", "-n") { DefaultValueFactory = _ => 20, Description = "Max results." };

        var cmd = new Command("search", "Run a keyword (FTS5/BM25) search.")
        {
            queryArg,
            limitOpt,
        };

        cmd.SetAction(parseResult =>
        {
            var query = parseResult.GetValue(queryArg)!;
            var limit = parseResult.GetValue(limitOpt);
            return Run(query, limit);
        });
        return cmd;
    }

    private static int Run(string query, int limit)
    {
        using var sp = CliServices.Build();
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        var search = sp.GetRequiredService<KeywordSearchService>();

        var hits = search.Search(query, limit);
        if (hits.Count == 0)
        {
            Console.WriteLine("(no matches)");
            return 0;
        }

        Console.WriteLine($"{hits.Count} result(s):\n");
        foreach (var h in hits)
        {
            var date = h.DateSent?.ToString("yyyy-MM-dd") ?? "          ";
            var from = h.FromName ?? h.FromAddress ?? "(unknown)";
            Console.WriteLine($"[{h.Bm25Score,7:F2}]  {date}  {h.Folder,-20}  {from}");
            Console.WriteLine($"           {h.Subject ?? "(no subject)"}");
            if (!string.IsNullOrEmpty(h.Snippet))
            {
                Console.WriteLine($"           {h.Snippet}");
            }
            Console.WriteLine();
        }
        return 0;
    }
}
