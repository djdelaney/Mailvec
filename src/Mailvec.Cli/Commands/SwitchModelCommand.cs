using System.CommandLine;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mailvec.Cli.Commands;

/// <summary>
/// The sanctioned way to move a database to a different embedding model:
/// drops and recreates the vec0 table at the new dimension, clears chunks,
/// re-queues every message, and updates the metadata the embedder validates
/// on startup. Destructive (all vectors are lost) — defaults to a y/N prompt.
/// Model/dims default to the bound Ollama options so the DB can never be
/// switched to something other than what the embedder will actually run.
/// </summary>
internal static class SwitchModelCommand
{
    public static Command Build()
    {
        var modelOpt = new Option<string?>("--model") { Description = "Target embedding model. Defaults to Ollama:EmbeddingModel from config/env." };
        var dimsOpt = new Option<int?>("--dims") { Description = "Target embedding dimensions. Defaults to Ollama:EmbeddingDimensions from config/env." };
        var yesOpt = new Option<bool>("--yes", "-y") { Description = "Skip the y/N confirmation prompt." };

        var cmd = new Command("switch-model", "Rebuild the vector index for a different embedding model. Deletes all chunks/vectors and re-queues every message.")
        {
            modelOpt,
            dimsOpt,
            yesOpt,
        };

        cmd.SetAction(parse =>
        {
            using var sp = CliServices.Build();
            return Execute(sp, parse.GetValue(modelOpt), parse.GetValue(dimsOpt), parse.GetValue(yesOpt), Console.Out, () => Console.ReadLine());
        });
        return cmd;
    }

    /// <summary>Test seam — see <see cref="PurgeDeletedCommand"/> for the pattern.</summary>
    internal static int Execute(IServiceProvider sp, string? model, int? dims, bool yes, TextWriter @out, Func<string?> readLine)
    {
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        var metadata = sp.GetRequiredService<MetadataRepository>();
        var ollama = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;

        var targetModel = model ?? ollama.EmbeddingModel;
        var targetDims = dims ?? ollama.EmbeddingDimensions;

        var currentModel = metadata.Get("embedding_model");
        var currentDims = metadata.Get("embedding_dimensions");

        if (currentModel == targetModel && currentDims == targetDims.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            @out.WriteLine($"Database is already on {targetModel} ({targetDims}d). Nothing to do.");
            return 0;
        }

        var (chunkCount, messageCount) = ReadCounts(sp);
        @out.WriteLine($"Current: {currentModel ?? "(not set)"} ({currentDims ?? "?"}d)");
        @out.WriteLine($"Target:  {targetModel} ({targetDims}d)");
        @out.WriteLine($"This deletes {chunkCount:N0} chunk(s) + their vectors and re-queues {messageCount:N0} message(s) for embedding.");

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

        var result = sp.GetRequiredService<SchemaMigrator>().SwitchEmbeddingModel(targetModel, targetDims);

        @out.WriteLine($"Switched {result.OldModel ?? "(not set)"} ({result.OldDimensions ?? "?"}d) -> {targetModel} ({targetDims}d).");
        @out.WriteLine($"{result.ChunksDeleted:N0} chunk(s) dropped; {result.MessagesReset:N0} message(s) re-queued.");
        @out.WriteLine();
        @out.WriteLine("Next steps:");
        @out.WriteLine($"  1. ollama pull {targetModel}");
        @out.WriteLine($"  2. Make sure the embedder runs with Ollama:EmbeddingModel={targetModel} and");
        @out.WriteLine($"     Ollama:EmbeddingDimensions={targetDims} (Ollama__* env vars or appsettings.Local.json)");
        @out.WriteLine("  3. Start the embedder to rebuild vectors (dotnet run --project src/Mailvec.Embedder");
        @out.WriteLine("     with the same env vars for an experiment DB; ops/redeploy.sh embedder for the live DB).");
        @out.WriteLine("  4. After the re-embed completes, VACUUM the database. The drop+rebuild leaves the new");
        @out.WriteLine("     vectors fragmented across freed pages, which makes KNN scans ~10x slower until then:");
        @out.WriteLine("       sqlite3 <db-path> 'VACUUM;'   # with services stopped, or VACUUM INTO a new file");
        @out.WriteLine();
        @out.WriteLine("Note: if this is the live database and the shared config still names the old model,");
        @out.WriteLine("the launchd embedder will refuse to start until the config matches (by design).");
        return 0;
    }

    private static (long Chunks, long Messages) ReadCounts(IServiceProvider sp)
    {
        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM chunks), (SELECT COUNT(*) FROM messages)";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
