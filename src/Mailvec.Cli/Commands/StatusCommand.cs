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
        var archive = sp.GetRequiredService<IOptions<ArchiveOptions>>().Value;
        var ollama = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
        var metadata = sp.GetRequiredService<MetadataRepository>();

        var (total, deleted, embedded, chunkCount) = ReadCounts(conn);
        var schemaModel = metadata.Get("embedding_model") ?? "(not set)";
        var schemaDim = metadata.Get("embedding_dimensions") ?? "(not set)";

        Console.WriteLine($"Database:    {Mailvec.Core.PathExpansion.Expand(archive.DatabasePath)}");
        Console.WriteLine($"Maildir:     {Mailvec.Core.PathExpansion.Expand(archive.MaildirRoot)}");
        Console.WriteLine();
        Console.WriteLine($"Messages:    {total:N0} total, {deleted:N0} deleted");
        Console.WriteLine($"Embeddings:  {embedded:N0} / {Math.Max(total - deleted, 0):N0} ({Coverage(embedded, total - deleted)})  [{chunkCount:N0} chunks]");
        Console.WriteLine();
        Console.WriteLine($"Embed model: schema={schemaModel} ({schemaDim}d)  config={ollama.EmbeddingModel} ({ollama.EmbeddingDimensions}d)");
        if (schemaModel != "(not set)" && schemaModel != ollama.EmbeddingModel)
        {
            Console.WriteLine("⚠  Schema/config mismatch — the embedder will refuse to start. Run `mailvec reindex --all` to switch models.");
        }
        return 0;
    }

    private static (long Total, long Deleted, long Embedded, long Chunks) ReadCounts(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM messages),
              (SELECT COUNT(*) FROM messages WHERE deleted_at IS NOT NULL),
              (SELECT COUNT(*) FROM messages WHERE embedded_at IS NOT NULL AND deleted_at IS NULL),
              (SELECT COUNT(*) FROM chunks)
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private static string Coverage(long covered, long total) =>
        total == 0 ? "n/a" : $"{(double)covered / total:P0}";
}
