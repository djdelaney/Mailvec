using System.CommandLine;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mailvec.Cli.Commands;

internal static class StatusCommand
{
    public static Command Build()
    {
        var cmd = new Command("status", "Show archive counts and embedding coverage.");
        cmd.SetAction(_ => Run());
        return cmd;
    }

    private static int Run()
    {
        using var sp = CliServices.Build();
        var migrator = sp.GetRequiredService<SchemaMigrator>();
        migrator.EnsureUpToDate();

        var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        var opts = sp.GetRequiredService<IOptions<ArchiveOptions>>().Value;

        var (total, deleted, embedded) = ReadCounts(conn);

        Console.WriteLine($"Database:    {Mailvec.Core.PathExpansion.Expand(opts.DatabasePath)}");
        Console.WriteLine($"Maildir:     {Mailvec.Core.PathExpansion.Expand(opts.MaildirRoot)}");
        Console.WriteLine();
        Console.WriteLine($"Messages:    {total:N0} total, {deleted:N0} deleted");
        Console.WriteLine($"Embeddings:  {embedded:N0} / {Math.Max(total - deleted, 0):N0} ({Coverage(embedded, total - deleted)})");
        return 0;
    }

    private static (long Total, long Deleted, long Embedded) ReadCounts(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM messages),
              (SELECT COUNT(*) FROM messages WHERE deleted_at IS NOT NULL),
              (SELECT COUNT(*) FROM messages WHERE embedded_at IS NOT NULL AND deleted_at IS NULL)
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static string Coverage(long covered, long total) =>
        total == 0 ? "n/a" : $"{(double)covered / total:P0}";
}
