using System.CommandLine;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mailvec.Cli.Commands;

internal static class SearchCommand
{
    public static Command Build()
    {
        var queryArg = new Argument<string>("query") { Description = "FTS5 query, or natural-language phrase for --semantic / --hybrid." };
        var limitOpt = new Option<int>("--limit", "-n") { DefaultValueFactory = _ => 20, Description = "Max results." };
        var semanticOpt = new Option<bool>("--semantic") { Description = "Use vector similarity instead of keyword search (requires Ollama + embeddings)." };
        var hybridOpt = new Option<bool>("--hybrid") { Description = "Combine keyword + vector with reciprocal rank fusion." };
        var titlesOnlyOpt = new Option<bool>("--titles-only", "-t") { Description = "Suppress snippet/body output; show only ranking, headers, and subject." };
        var withIdOpt = new Option<bool>("--with-id", "-i") { Description = "Show each result's RFC 5322 Message-ID. Useful when sourcing IDs for `eval-add --pin-relevant`." };

        var cmd = new Command("search", "Search the archive (keyword by default).")
        {
            queryArg,
            limitOpt,
            semanticOpt,
            hybridOpt,
            titlesOnlyOpt,
            withIdOpt,
        };

        cmd.SetAction(async parseResult =>
        {
            var query = parseResult.GetValue(queryArg)!;
            var limit = parseResult.GetValue(limitOpt);
            var semantic = parseResult.GetValue(semanticOpt);
            var hybrid = parseResult.GetValue(hybridOpt);
            var titlesOnly = parseResult.GetValue(titlesOnlyOpt);
            var withId = parseResult.GetValue(withIdOpt);

            if (semantic && hybrid)
            {
                Console.Error.WriteLine("--semantic and --hybrid are mutually exclusive.");
                return 2;
            }

            return await Run(query, limit, semantic, hybrid, titlesOnly, withId);
        });
        return cmd;
    }

    private static async Task<int> Run(string query, int limit, bool semantic, bool hybrid, bool titlesOnly, bool withId)
    {
        using var sp = CliServices.Build();
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        var fastmail = sp.GetRequiredService<IOptions<FastmailOptions>>().Value;

        if (hybrid)
        {
            var search = sp.GetRequiredService<HybridSearchService>();
            var hits = await search.SearchAsync(query, limit);
            PrintHybrid(hits, titlesOnly, withId, fastmail);
            return 0;
        }

        if (semantic)
        {
            var search = sp.GetRequiredService<VectorSearchService>();
            var hits = await search.SearchAsync(query, limit);
            PrintVector(hits, titlesOnly, withId, fastmail);
            return 0;
        }

        var keyword = sp.GetRequiredService<KeywordSearchService>();
        PrintKeyword(keyword.Search(query, limit), titlesOnly, withId, fastmail);
        return 0;
    }

    private static void PrintKeyword(IReadOnlyList<Mailvec.Core.Models.SearchHit> hits, bool titlesOnly, bool withId, FastmailOptions fastmail)
    {
        if (hits.Count == 0) { Console.WriteLine("(no matches)"); return; }
        Console.WriteLine($"{hits.Count} result(s):\n");
        foreach (var h in hits)
        {
            var date = h.DateSent?.ToString("yyyy-MM-dd") ?? "          ";
            var from = h.FromName ?? h.FromAddress ?? "(unknown)";
            Console.WriteLine($"[bm25 {h.Bm25Score,7:F2}]  {Bold(h.Subject ?? "(no subject)")}");
            Console.WriteLine($"                  {Dim($"{date}  {h.Folder}  ·  {from}")}");
            if (!titlesOnly && !string.IsNullOrEmpty(h.Snippet)) Console.WriteLine($"                  {Dim(h.Snippet)}");
            if (withId) Console.WriteLine($"                  {Dim($"id: {h.MessageIdHeader}")}");
            PrintWebmailUrl(h.MessageIdHeader, fastmail);
            Console.WriteLine();
        }
    }

    private static void PrintVector(IReadOnlyList<VectorHit> hits, bool titlesOnly, bool withId, FastmailOptions fastmail)
    {
        if (hits.Count == 0) { Console.WriteLine("(no matches)"); return; }
        Console.WriteLine($"{hits.Count} result(s):\n");
        foreach (var h in hits)
        {
            var date = h.DateSent?.ToString("yyyy-MM-dd") ?? "          ";
            var from = h.FromName ?? h.FromAddress ?? "(unknown)";
            Console.WriteLine($"[dist {h.Distance,7:F3}]  {Bold(h.Subject ?? "(no subject)")}");
            Console.WriteLine($"                  {Dim($"{date}  {h.Folder}  ·  {from}")}");
            if (!titlesOnly) Console.WriteLine($"                  {Dim(Truncate(h.ChunkText, 240))}");
            if (withId) Console.WriteLine($"                  {Dim($"id: {h.MessageIdHeader}")}");
            PrintWebmailUrl(h.MessageIdHeader, fastmail);
            Console.WriteLine();
        }
    }

    private static void PrintHybrid(IReadOnlyList<HybridHit> hits, bool titlesOnly, bool withId, FastmailOptions fastmail)
    {
        if (hits.Count == 0) { Console.WriteLine("(no matches)"); return; }
        Console.WriteLine($"{hits.Count} result(s):\n");
        foreach (var h in hits)
        {
            var date = h.DateSent?.ToString("yyyy-MM-dd") ?? "          ";
            var from = h.FromName ?? h.FromAddress ?? "(unknown)";
            var legs = $"bm25={h.Bm25Rank?.ToString() ?? "-"} vec={h.VectorRank?.ToString() ?? "-"}";
            Console.WriteLine($"[rrf {h.RrfScore,6:F4}]  {Bold(h.Subject ?? "(no subject)")}");
            Console.WriteLine($"                  {Dim($"{date}  {h.Folder}  ·  {from}    ({legs})")}");
            if (!titlesOnly && !string.IsNullOrEmpty(h.Snippet)) Console.WriteLine($"                  {Dim(Truncate(h.Snippet, 240))}");
            if (withId) Console.WriteLine($"                  {Dim($"id: {h.MessageIdHeader}")}");
            PrintWebmailUrl(h.MessageIdHeader, fastmail);
            Console.WriteLine();
        }
    }

    /// <summary>Emits the Fastmail deep-link if AccountId is configured; silent otherwise.</summary>
    private static void PrintWebmailUrl(string messageIdHeader, FastmailOptions fastmail)
    {
        var url = WebmailLinkBuilder.Build(messageIdHeader, fastmail);
        if (url is not null) Console.WriteLine($"                  {Dim(url)}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static readonly bool UseColor = !Console.IsOutputRedirected;
    private static string Bold(string s) => UseColor ? $"\x1b[1m{s}\x1b[0m" : s;
    private static string Dim(string s) => UseColor ? $"\x1b[2m{s}\x1b[0m" : s;
}
