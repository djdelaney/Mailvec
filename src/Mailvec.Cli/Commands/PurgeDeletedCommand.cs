using System.CommandLine;
using Mailvec.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Hard-deletes every message marked deleted_at IS NOT NULL, along with its
/// chunks, vectors, attachments, and FTS rows. Soft-deletes accumulate as the
/// indexer reconciles the Maildir against the DB; this command is the only
/// way to actually remove them. Irreversible — defaults to a y/N confirmation
/// prompt.
///
/// Note: SQLite reuses freed pages on subsequent inserts but won't shrink the
/// file on its own. The hint at the end of a successful run points users at
/// `mailvec checkpoint` (WAL flush) and a manual VACUUM (file-size reclaim).
/// </summary>
internal static class PurgeDeletedCommand
{
    public static Command Build()
    {
        var yesOpt = new Option<bool>("--yes", "-y") { Description = "Skip the y/N confirmation prompt." };
        var dryRunOpt = new Option<bool>("--dry-run") { Description = "Show how many rows would be purged without modifying the DB." };

        var cmd = new Command("purge-deleted", "Hard-delete soft-deleted messages and their chunks/vectors/attachments. Irreversible.")
        {
            yesOpt,
            dryRunOpt,
        };

        cmd.SetAction(parse => Run(parse.GetValue(yesOpt), parse.GetValue(dryRunOpt)));
        return cmd;
    }

    private static int Run(bool yes, bool dryRun)
    {
        using var sp = CliServices.Build();
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        var messages = sp.GetRequiredService<MessageRepository>();

        var count = messages.CountSoftDeleted();
        if (count == 0)
        {
            Console.WriteLine("No soft-deleted messages to purge.");
            return 0;
        }

        Console.WriteLine($"{count:N0} soft-deleted message(s) will be hard-deleted, along with their chunks, vectors, attachments, and FTS entries.");

        if (dryRun)
        {
            Console.WriteLine("Dry run — no changes made.");
            return 0;
        }

        if (!yes)
        {
            Console.Write("This is irreversible. Proceed? [y/N]: ");
            var input = Console.ReadLine();
            if (!string.Equals(input?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Aborted.");
                return 1;
            }
        }

        var purged = messages.PurgeSoftDeleted();
        Console.WriteLine($"Purged {purged:N0} message(s).");
        Console.WriteLine();
        Console.WriteLine("Freed pages stay inside the SQLite file. To reclaim disk space:");
        Console.WriteLine("  1. mailvec checkpoint            # flush the WAL");
        Console.WriteLine("  2. stop indexer/embedder/mcp     # VACUUM needs an exclusive moment");
        Console.WriteLine("  3. sqlite3 <db-path> 'VACUUM;'   # rewrites the file without freed pages");
        return 0;
    }
}
