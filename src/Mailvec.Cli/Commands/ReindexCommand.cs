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
        var yesOpt = new Option<bool>("--yes", "-y") { Description = "Skip the y/N confirmation prompt." };

        var cmd = new Command("reindex", "Clear embeddings so the embedder will re-process matching messages.")
        {
            allOpt,
            folderOpt,
            yesOpt,
        };

        cmd.SetAction(parse =>
        {
            var all = parse.GetValue(allOpt);
            var folder = parse.GetValue(folderOpt);
            var yes = parse.GetValue(yesOpt);
            return ValidateAndRun(all, folder, yes, Console.Out, Console.Error, () => Console.ReadLine());
        });
        return cmd;
    }

    /// <summary>Test seam — see <see cref="PurgeDeletedCommand"/> for the pattern.</summary>
    internal static int ValidateAndRun(bool all, string? folder, bool yes, TextWriter @out, TextWriter err, Func<string?> readLine)
    {
        if (!all && folder is null)
        {
            err.WriteLine("Specify --all or --folder=<name>.");
            return 2;
        }
        using var sp = CliServices.Build();
        return Execute(sp, all ? null : folder, yes, @out, readLine);
    }

    /// <summary>Test seam that lets tests inject a pre-built provider.</summary>
    internal static int Execute(IServiceProvider sp, string? folder, bool yes, TextWriter @out, Func<string?> readLine)
    {
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        var chunks = sp.GetRequiredService<ChunkRepository>();
        var scope = folder is null ? "ALL messages" : $"folder '{folder}'";

        // Confirm like purge-deleted and switch-model do: dropping every
        // chunk + vector commits the archive to hours-to-days of local
        // re-embedding, and one mistyped command shouldn't do that silently.
        if (!yes)
        {
            @out.Write($"This clears every chunk and vector for {scope}; re-embedding a large archive takes hours to days. Proceed? [y/N]: ");
            var input = readLine();
            if (!string.Equals(input?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                @out.WriteLine("Aborted.");
                return 1;
            }
        }

        var affected = chunks.ClearEmbeddings(folderFilter: folder);
        @out.WriteLine($"Cleared embeddings on {affected} messages ({(folder is null ? "all messages" : $"folder '{folder}'")}). The embedder will re-process them on its next poll.");
        return 0;
    }
}
