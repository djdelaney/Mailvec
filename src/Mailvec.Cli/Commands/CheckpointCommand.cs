using System.CommandLine;
using Mailvec.Core;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Force a WAL checkpoint against the archive DB. Useful after the initial
/// bulk embed run, where the WAL can grow into multi-GB territory faster than
/// SQLite's automatic checkpoint (every 1000 frames) reclaims it. TRUNCATE
/// mode resets the WAL file to zero bytes on success — pure win for backups
/// and disk usage, no effect on the main DB contents.
///
/// Note: TRUNCATE needs an exclusive moment with no readers. If indexer /
/// embedder / MCP are holding the DB, the call returns busy=1 and we fall
/// back to a PASSIVE checkpoint, which still flushes frames into main but
/// can't truncate the WAL file. Stop the services first if you really want
/// the file shrunk.
/// </summary>
internal static class CheckpointCommand
{
    public static Command Build()
    {
        var cmd = new Command("checkpoint", "Force a WAL checkpoint and (when possible) truncate the -wal file.");
        cmd.SetAction(_ => Run());
        return cmd;
    }

    private static int Run()
    {
        using var sp = CliServices.Build();
        return Execute(sp, Console.Out);
    }

    /// <summary>Test seam — see <see cref="PurgeDeletedCommand"/> for the pattern.</summary>
    internal static int Execute(IServiceProvider sp, TextWriter @out)
    {
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();

        var dbPath = PathExpansion.Expand(sp.GetRequiredService<IOptions<ArchiveOptions>>().Value.DatabasePath);
        var walPath = dbPath + "-wal";
        var sizeBefore = WalSize(walPath);

        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();

        // Sanity-check the journal mode first. wal_checkpoint(TRUNCATE) returns
        // (-1, -1, -1) on a non-WAL DB, which is uninformative; a clear error
        // is more useful than three "-1" values.
        using (var modeCmd = conn.CreateCommand())
        {
            modeCmd.CommandText = "PRAGMA journal_mode;";
            var mode = modeCmd.ExecuteScalar() as string ?? "";
            if (!mode.Equals("wal", StringComparison.OrdinalIgnoreCase))
            {
                @out.WriteLine($"Database:    {dbPath}");
                @out.WriteLine($"Journal:     {mode} (not WAL — checkpoint is a no-op)");
                @out.WriteLine();
                @out.WriteLine("Nothing to checkpoint. The DB will switch to WAL on next ConnectionFactory.Open;");
                @out.WriteLine("re-run this command after that to confirm.");
                return 0;
            }
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var busy = reader.GetInt64(0);
        var logFrames = reader.GetInt64(1);
        var checkpointed = reader.GetInt64(2);

        var sizeAfter = WalSize(walPath);

        @out.WriteLine($"Database:      {dbPath}");
        @out.WriteLine($"WAL before:    {Format(sizeBefore)}");
        @out.WriteLine($"WAL after:     {Format(sizeAfter)}");
        @out.WriteLine($"Frames synced: {checkpointed:N0}");
        if (busy != 0)
        {
            @out.WriteLine();
            @out.WriteLine("⚠  Could not truncate (busy=1). A reader was holding the DB; pages were flushed");
            @out.WriteLine("   into main but the -wal file kept its allocation. Stop the indexer / embedder /");
            @out.WriteLine("   MCP server and re-run if you want the file shrunk to zero.");
            return 1;
        }
        if (logFrames != 0)
        {
            @out.WriteLine($"WAL frames remaining: {logFrames:N0} (TRUNCATE may have been deferred).");
        }
        return 0;
    }

    private static long WalSize(string walPath) =>
        File.Exists(walPath) ? new FileInfo(walPath).Length : 0;

    private static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
