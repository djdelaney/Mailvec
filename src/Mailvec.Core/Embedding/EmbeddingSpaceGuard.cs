using Mailvec.Core.Data;

namespace Mailvec.Core.Embedding;

/// <summary>
/// Read-side embedding-space enforcement (review finding: the write side
/// refused drift five ways while semantic search served through it). Called
/// before query embedding/KNN: a query embedded under the active profile is
/// only comparable to stored vectors from the SAME space, and serving a
/// cross-space ranking is plausible-looking garbage — e.g. an edited
/// `Ollama:QueryInstructionPrefix` changes query vectors without changing
/// their dimensions, so nothing downstream would ever error.
///
/// <para>Deliberately METADATA-ONLY: model, dimensions, space id and config
/// hash are one cheap SQLite read each and definitive. The artifact digest
/// and future hosted sentinels stay on the embedder/health cadence — a
/// network probe inside every search would put an availability dependency on
/// the hot path, and "unknown is never drift" means a flaky tags endpoint
/// must not take down semantic search.</para>
///
/// <para>Absent metadata passes: a fresh database has no vectors for a wrong
/// answer to come from, and unknown ≠ mismatch is the rule everywhere else.</para>
/// </summary>
public sealed class EmbeddingSpaceGuard(MetadataRepository metadata, ResolvedEmbeddingProfile profile)
{
    public void VerifyReadCompatible()
    {
        var (spaceId, configHash) = EmbeddingSpace.ForProfile(profile);

        Check("embedding_model", profile.WireModel);
        Check("embedding_dimensions",
            profile.OutputDimensions.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Check(EmbeddingSpace.SpaceIdKey, spaceId);
        Check(EmbeddingSpace.ConfigHashKey, configHash);
    }

    private void Check(string key, string expected)
    {
        var stored = metadata.Get(key);
        if (stored is null || string.Equals(stored, expected, StringComparison.Ordinal)) return;

        // Values here are model names, derived space ids and hash hex — never
        // an endpoint, key, or mail content. The MCP layer still translates
        // this into its own client-facing message; this one is for logs.
        throw new EmbeddingException(EmbeddingFailureKind.SpaceMismatch,
            $"Embedding-space mismatch on '{key}': stored vectors carry '{stored}' but the active " +
            $"profile describes '{expected}'. Semantic results would be meaningless; keyword search is " +
            "unaffected. Revert the configuration, or run `mailvec switch-model` to re-embed.");
    }
}
