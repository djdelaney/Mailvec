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
    /// <summary>
    /// Grace period before a soft-delete becomes purgeable. A scan hitting
    /// transient DB errors can briefly soft-delete a live message (it
    /// self-heals on the next scan); the grace period keeps a purge from
    /// hard-deleting inside that window. Override with --min-age-minutes 0
    /// to purge everything.
    /// </summary>
    internal const int DefaultMinAgeMinutes = 60;

    /// <summary>
    /// A single scan that soft-deleted at least this many messages is treated
    /// as a possible Maildir problem, not a decision: the purge refuses it
    /// unless <c>--allow-mass-deletion</c> is passed. Matches the scanner's
    /// <c>Indexer:MassDeletionMinimum</c> default; the day-to-day rate is tens.
    /// </summary>
    internal const int MassDeletionBatch = 500;

    public static Command Build()
    {
        var yesOpt = new Option<bool>("--yes", "-y") { Description = "Skip the y/N confirmation prompt." };
        var dryRunOpt = new Option<bool>("--dry-run") { Description = "Show how many rows would be purged without modifying the DB." };
        var minAgeOpt = new Option<int>("--min-age-minutes")
        {
            Description = "Only purge messages soft-deleted at least this many minutes ago (0 = purge all). " +
                          "The default protects messages a struggling scan may have soft-deleted by mistake.",
            DefaultValueFactory = _ => DefaultMinAgeMinutes,
        };

        var allowMassOpt = new Option<bool>("--allow-mass-deletion")
        {
            Description = $"Required to purge when a single indexer scan soft-deleted {MassDeletionBatch}+ messages. " +
                          "Check that the Maildir is intact first: a folder move or a half-mounted volume looks exactly like that.",
        };

        var cmd = new Command("purge-deleted", "Hard-delete soft-deleted messages and their chunks/vectors/attachments. Irreversible.")
        {
            yesOpt,
            dryRunOpt,
            minAgeOpt,
            allowMassOpt,
        };

        cmd.SetAction(parse => Run(parse.GetValue(yesOpt), parse.GetValue(dryRunOpt), parse.GetValue(minAgeOpt), parse.GetValue(allowMassOpt)));
        return cmd;
    }

    private static int Run(bool yes, bool dryRun, int minAgeMinutes, bool allowMass)
    {
        using var sp = CliServices.Build();
        return Execute(sp, yes, dryRun, Console.Out, () => Console.ReadLine(), minAgeMinutes, allowMass);
    }

    /// <summary>
    /// Test seam: lets tests inject a pre-built <see cref="IServiceProvider"/>
    /// (typically backed by a temp DB), capture stdout via a custom writer,
    /// and script the y/N prompt. The CLI wrapper above passes the standard
    /// Console.Out + Console.ReadLine.
    /// </summary>
    internal static int Execute(IServiceProvider sp, bool yes, bool dryRun, TextWriter @out, Func<string?> readLine, int minAgeMinutes = DefaultMinAgeMinutes, bool allowMassDeletion = false)
    {
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        var messages = sp.GetRequiredService<MessageRepository>();

        DateTimeOffset? cutoff = minAgeMinutes > 0
            ? DateTimeOffset.UtcNow.AddMinutes(-minAgeMinutes)
            : null;

        var count = messages.CountSoftDeleted(cutoff);
        var total = cutoff is null ? count : messages.CountSoftDeleted();
        var skippedRecent = total - count;

        if (count == 0)
        {
            @out.WriteLine(skippedRecent > 0
                ? $"No soft-deleted messages older than {minAgeMinutes} minute(s) to purge ({skippedRecent:N0} more recent one(s) skipped; use --min-age-minutes 0 to include them)."
                : "No soft-deleted messages to purge.");
            return 0;
        }

        @out.WriteLine($"{count:N0} soft-deleted message(s) will be hard-deleted, along with their chunks, vectors, attachments, and FTS entries.");
        if (skippedRecent > 0)
        {
            @out.WriteLine($"{skippedRecent:N0} message(s) soft-deleted within the last {minAgeMinutes} minute(s) are skipped (use --min-age-minutes 0 to include them).");
        }

        // Soft-deletes are recoverable (a row resurrects when its file
        // reappears); this purge is the step that is not. A large batch from
        // one scan is the signature of a partly missing Maildir, which is why
        // --yes alone does not get past it.
        var largest = messages.LargestSoftDeleteBatch(cutoff);
        var mass = largest is { Count: >= MassDeletionBatch };
        if (mass)
        {
            @out.WriteLine(
                $"WARNING: {largest!.Value.Count:N0} of these were soft-deleted by a single indexer scan at " +
                $"{largest.Value.DeletedAt:u}. That is the signature of a Maildir that was partly missing (a folder " +
                "move, a half-mounted volume, a partial restore), not only of a deliberate bulk delete. Confirm the " +
                "mail is really gone from the server before purging — if the files come back, the indexer restores " +
                "these rows on its own, but not after a purge.");
        }

        if (dryRun)
        {
            @out.WriteLine("Dry run — no changes made.");
            return 0;
        }

        if (mass && !allowMassDeletion)
        {
            @out.WriteLine("Refusing: re-run with --allow-mass-deletion once you have confirmed the deletion is real.");
            return 1;
        }

        if (!yes)
        {
            @out.Write("This is irreversible. Proceed? [y/N]: ");
            var input = readLine();
            if (!string.Equals(input?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                @out.WriteLine("Aborted.");
                return 1;
            }
        }

        var purged = messages.PurgeSoftDeleted(cutoff);
        @out.WriteLine($"Purged {purged:N0} message(s).");
        @out.WriteLine();
        @out.WriteLine("Freed pages stay inside the SQLite file. To reclaim disk space:");
        @out.WriteLine("  1. mailvec checkpoint            # flush the WAL");
        @out.WriteLine("  2. stop indexer/embedder/mcp     # VACUUM needs an exclusive moment");
        @out.WriteLine("  3. sqlite3 <db-path> 'VACUUM;'   # rewrites the file without freed pages");
        return 0;
    }
}
