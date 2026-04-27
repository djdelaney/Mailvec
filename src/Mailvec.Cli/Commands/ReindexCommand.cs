using System.CommandLine;
using Mailvec.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Commands;

internal static class ReindexCommand
{
    public static Command Build()
    {
        var allOpt = new Option<bool>("--all") { Description = "Clear embeddings for every message." };
        var folderOpt = new Option<string?>("--folder") { Description = "Limit to a single folder, e.g. INBOX or Archive.2024." };

        var cmd = new Command("reindex", "Clear embeddings so the embedder will re-process matching messages.")
        {
            allOpt,
            folderOpt,
        };

        cmd.SetAction(parse =>
        {
            var all = parse.GetValue(allOpt);
            var folder = parse.GetValue(folderOpt);
            if (!all && folder is null)
            {
                Console.Error.WriteLine("Specify --all or --folder=<name>.");
                return 2;
            }
            return Run(all ? null : folder);
        });
        return cmd;
    }

    private static int Run(string? folder)
    {
        using var sp = CliServices.Build();
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        var chunks = sp.GetRequiredService<ChunkRepository>();

        var affected = chunks.ClearEmbeddings(folderFilter: folder);
        var scope = folder is null ? "all messages" : $"folder '{folder}'";
        Console.WriteLine($"Cleared embeddings on {affected} messages ({scope}). The embedder will re-process them on its next poll.");
        return 0;
    }
}
