using System.CommandLine;
using Mailvec.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Fixes database inconsistencies that accumulate over time. Counterpart to
/// `mailvec doctor` — doctor diagnoses, repair acts. Designed to grow: each
/// distinct cleanup runs as its own step here so a future entry (stranded
/// attachment rows, stale sync_state, FTS drift, …) can be appended without
/// changing the command surface.
///
/// Current repairs:
///   * Orphan vec0 rows — chunk_embeddings entries whose chunk_id is no
///     longer in chunks. These cause the embedder to fail with
///     UNIQUE-constraint violations on the next rowid collision.
///
/// Idempotent. Safe to run while services are up — uses short transactions
/// against the same DB the embedder writes to. `--dry-run` reports what
/// would be repaired without changing anything.
/// </summary>
internal static class RepairCommand
{
    public static Command Build()
    {
        var dryRunOpt = new Option<bool>("--dry-run") { Description = "Report what would be repaired without changing the DB." };

        var cmd = new Command("repair", "Fix database inconsistencies (orphan vectors, etc).")
        {
            dryRunOpt,
        };
        cmd.SetAction(parse => Run(parse.GetValue(dryRunOpt)));
        return cmd;
    }

    private static int Run(bool dryRun)
    {
        using var sp = CliServices.Build();
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();

        var totalIssues = 0;
        var totalRepaired = 0;

        // ----- Orphan vec0 rows -----
        var chunks = sp.GetRequiredService<ChunkRepository>();
        Console.WriteLine("Orphan vectors");
        var orphans = chunks.CountOrphanEmbeddings();
        if (orphans == 0)
        {
            Console.WriteLine("  none");
        }
        else
        {
            totalIssues += orphans;
            Console.WriteLine($"  found {orphans:N0} chunk_embeddings row(s) without a matching chunks row");
            if (dryRun)
            {
                Console.WriteLine("  dry run — nothing deleted");
            }
            else
            {
                var deleted = chunks.DeleteOrphanEmbeddings();
                totalRepaired += deleted;
                Console.WriteLine($"  deleted {deleted:N0}");
            }
        }

        Console.WriteLine();
        if (totalIssues == 0)
        {
            Console.WriteLine("No repairs needed.");
        }
        else if (dryRun)
        {
            Console.WriteLine($"Dry run: would repair {totalIssues:N0} item(s).");
        }
        else
        {
            Console.WriteLine($"Repaired {totalRepaired:N0} item(s).");
        }
        return 0;
    }
}
