using System.CommandLine;
using Mailvec.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Commands;

internal static class RebuildFtsCommand
{
    public static Command Build()
    {
        var cmd = new Command("rebuild-fts", "Drop and rebuild the FTS5 index from messages.");
        cmd.SetAction(_ => Run());
        return cmd;
    }

    private static int Run()
    {
        using var sp = CliServices.Build();
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();

        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        // FTS5's 'rebuild' command rebuilds the contentless / external-content index from base table.
        cmd.CommandText = "INSERT INTO messages_fts(messages_fts) VALUES('rebuild');";
        cmd.ExecuteNonQuery();
        Console.WriteLine("FTS5 index rebuilt.");
        return 0;
    }
}
