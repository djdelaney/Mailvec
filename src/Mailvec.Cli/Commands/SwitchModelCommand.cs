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
        var forceOpt = new Option<bool>("--force") { Description = "Rebuild even when model and dimensions are unchanged — the remedy when the model ARTIFACT changed under its name (embedding_model_digest mismatch: the tag was re-pulled with different weights)." };

        var cmd = new Command("switch-model", "Rebuild the vector index for a different embedding model. Deletes all chunks/vectors and re-queues every message.")
        {
            modelOpt,
            dimsOpt,
            yesOpt,
            forceOpt,
        };

        cmd.SetAction(parse =>
        {
            using var sp = CliServices.Build();
            return Execute(sp, parse.GetValue(modelOpt), parse.GetValue(dimsOpt), parse.GetValue(yesOpt), Console.Out, () => Console.ReadLine(), parse.GetValue(forceOpt));
        });
        return cmd;
    }

    /// <summary>Test seam — see <see cref="PurgeDeletedCommand"/> for the pattern.</summary>
    internal static int Execute(IServiceProvider sp, string? model, int? dims, bool yes, TextWriter @out, Func<string?> readLine, bool force = false)
    {
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        var metadata = sp.GetRequiredService<MetadataRepository>();
        var profile = sp.GetRequiredService<Mailvec.Core.Embedding.ResolvedEmbeddingProfile>();

        var targetModel = model ?? profile.WireModel;
        var targetDims = dims ?? profile.OutputDimensions;

        // Hosted profiles assert their space id, so a --model/--dims override
        // that diverges from the active profile has no honest identity to
        // stamp — an Ollama-style derivation of a hosted wire model would be
        // refused by the embedder this migration feeds. Ollama profiles keep
        // override freedom (the embedding-experiments flow).
        if (profile.Protocol == Mailvec.Core.Embedding.EmbeddingRegistration.OpenAiCompatibleProtocol
            && (targetModel != profile.WireModel || targetDims != profile.OutputDimensions))
        {
            @out.WriteLine($"--model/--dims must match the active hosted profile '{profile.Name}' " +
                $"({profile.WireModel} @{profile.OutputDimensions}d). Hosted identity is asserted by the " +
                "profile's SpaceId — change the profile configuration, not the flags.");
            return 1;
        }

        var currentModel = metadata.Get("embedding_model");
        var currentDims = metadata.Get("embedding_dimensions");

        // No-op requires the COMPLETE identity to match, not just model+dims:
        // the same nominal model at the same width on a different provider
        // (an asserted hosted SpaceId), or with changed text transforms (a
        // different config hash), is a different vector space that needs the
        // full rebuild. Model+dims alone reported "nothing to do" for exactly
        // the cloud-hosted-same-model move this feature exists to support,
        // leaving the operator wedged between a no-op and refusing guards.
        var (targetSpaceId, targetConfigHash) = sp.GetRequiredService<SchemaMigrator>()
            .TargetIdentity(targetModel, targetDims);
        var identityUnchanged =
            currentModel == targetModel
            && currentDims == targetDims.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && metadata.Get(Mailvec.Core.Embedding.EmbeddingSpace.SpaceIdKey) == targetSpaceId
            && metadata.Get(Mailvec.Core.Embedding.EmbeddingSpace.ConfigHashKey) == targetConfigHash;

        if (!force && identityUnchanged)
        {
            @out.WriteLine($"Database is already on {targetModel} ({targetDims}d) in space {targetSpaceId}. Nothing to do.");
            @out.WriteLine("(If the model ARTIFACT changed under this name — digest mismatch — rerun with --force to rebuild.)");
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
        if (profile.Protocol == Mailvec.Core.Embedding.EmbeddingRegistration.OpenAiCompatibleProtocol)
        {
            @out.WriteLine("  1. Make sure the API key is in place (secrets/embedding_api_key for compose;");
            @out.WriteLine("     the profile's Auth:ApiKeyFile for a local install).");
            @out.WriteLine("  2. Recreate the services so they see the current profile env (docker compose");
            @out.WriteLine("     up -d — NOT restart, env is fixed at container creation).");
            @out.WriteLine("  3. Watch `mailvec status` coverage; the embedder stamps sentinel fingerprints");
            @out.WriteLine("     on its first cycle and refuses if the provider's function drifts.");
        }
        else
        {
            @out.WriteLine($"  1. ollama pull {targetModel}");
            @out.WriteLine($"  2. Make sure the embedder runs with Ollama:EmbeddingModel={targetModel} and");
            @out.WriteLine($"     Ollama:EmbeddingDimensions={targetDims} (Ollama__* env vars or appsettings.Local.json)");
            @out.WriteLine("  3. Start the embedder to rebuild vectors (dotnet run --project src/Mailvec.Embedder");
            @out.WriteLine("     with the same env vars for an experiment DB; ops/redeploy.sh embedder for the live DB).");
        }
        @out.WriteLine("  4. After the re-embed completes, VACUUM the database. The drop+rebuild leaves the new");
        @out.WriteLine("     vectors fragmented across freed pages, which makes KNN scans ~10x slower until then:");
        @out.WriteLine("       sqlite3 <db-path> 'VACUUM;'   # with services stopped, or VACUUM INTO a new file");
        @out.WriteLine();
        @out.WriteLine("Note: services still configured with the OLD profile refuse to run against the");
        @out.WriteLine("switched database until their config matches (by design).");
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
