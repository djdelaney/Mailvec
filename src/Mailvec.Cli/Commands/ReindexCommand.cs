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
            return ValidateAndRun(all, folder, Console.Out, Console.Error);
        });
        return cmd;
    }

    /// <summary>Test seam — see <see cref="PurgeDeletedCommand"/> for the pattern.</summary>
    internal static int ValidateAndRun(bool all, string? folder, TextWriter @out, TextWriter err)
    {
        if (!all && folder is null)
        {
            err.WriteLine("Specify --all or --folder=<name>.");
            return 2;
        }
        using var sp = CliServices.Build();
        return Execute(sp, all ? null : folder, @out);
    }

    /// <summary>Test seam that lets tests inject a pre-built provider.</summary>
    internal static int Execute(IServiceProvider sp, string? folder, TextWriter @out)
    {
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        var chunks = sp.GetRequiredService<ChunkRepository>();

        var affected = chunks.ClearEmbeddings(folderFilter: folder);
        var scope = folder is null ? "all messages" : $"folder '{folder}'";
        @out.WriteLine($"Cleared embeddings on {affected} messages ({scope}). The embedder will re-process them on its next poll.");
        return 0;
    }
}
