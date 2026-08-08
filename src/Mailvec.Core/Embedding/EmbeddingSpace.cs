using System.Security.Cryptography;
using System.Text;
using Mailvec.Core.Options;

namespace Mailvec.Core.Embedding;

/// <summary>
/// Embedding-space identity (schema v11): the answer to "are these vectors
/// mutually comparable?", persisted in metadata alongside the model name and
/// dimension count that predate it.
///
/// <para><c>embedding_space_id</c> names the semantic space. For the Ollama
/// provider it is derived — <c>ollama:&lt;model&gt;:&lt;dimensions&gt;</c> —
/// because a locally pulled tag is operator-controlled; future hosted
/// profiles must assert it explicitly (see
/// docs/proposals/embedding-providers.md, "Embedding-space identity"), since
/// a wire model string is not proof of vector compatibility.</para>
///
/// <para><c>embedding_config_hash</c> proves how Mailvec invoked the space: a
/// SHA-256 over a canonical, versioned serialization of every locally known
/// vector-affecting setting — space id, wire model, dimensions, query/document
/// text transforms, normalization policy. It exists to catch the change the
/// space id can't: editing <c>Ollama:QueryInstructionPrefix</c> (or a future
/// document transform) moves query vectors out of the space the stored
/// document vectors live in while every name stays the same. Secrets,
/// timeouts, batch sizes and endpoint URLs are deliberately excluded — they
/// don't affect the vectors.</para>
/// </summary>
public static class EmbeddingSpace
{
    public const string SpaceIdKey = "embedding_space_id";
    public const string ConfigHashKey = "embedding_config_hash";

    /// <summary>
    /// The observed artifact digest of the serving model (Ollama manifest
    /// digest today) — the third leg of the identity: space id names the
    /// space, config hash proves the invocation, the digest pins the local
    /// artifact. An OBSERVATION, not configuration: stamped by the embedder
    /// when first seen, cleared by switch-model, absent = "not yet observed"
    /// (never a mismatch). Needs no schema migration for exactly that reason.
    /// </summary>
    public const string ModelDigestKey = "embedding_model_digest";

    /// <summary>
    /// The normalization policy token folded into the config hash. Bump the
    /// suffix if <see cref="VectorMath"/>'s contract ever changes — that IS a
    /// vector-affecting change and must invalidate the hash.
    /// </summary>
    public const string NormalizationPolicy = "l2-unit-v1";

    /// <summary>
    /// Derived space id for the Ollama provider:
    /// <c>ollama:&lt;model&gt;:&lt;dimensions&gt;</c>. The v11 migration stamps
    /// exactly this shape from a database's own stored metadata, so keep the
    /// format in lockstep with schema/migrations/011_embedding_space.sql.
    /// </summary>
    public static string LegacySpaceId(string model, int dimensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return $"ollama:{model}:{dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Canonical, versioned config hash covering all four text transforms.
    /// Field values are length-prefixed so no delimiter collision can make
    /// two different configurations serialize identically; the leading
    /// version token versions the serialization itself — v2 added the
    /// suffix fields before any v11 database shipped in a release, so no
    /// stored v1 hash exists outside development machines (a dev DB
    /// self-heals by deleting the stored key; the vectors are unaffected
    /// because every added field was empty under v1).
    /// </summary>
    public static string ComputeConfigHash(
        string spaceId, string wireModel, int dimensions,
        string queryPrefix, string querySuffix, string documentPrefix, string documentSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(wireModel);

        var sb = new StringBuilder();
        sb.Append("v2\n");
        AppendField(sb, "spaceId", spaceId);
        AppendField(sb, "wireModel", wireModel);
        AppendField(sb, "dimensions", dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendField(sb, "queryPrefix", queryPrefix);
        AppendField(sb, "querySuffix", querySuffix);
        AppendField(sb, "documentPrefix", documentPrefix);
        AppendField(sb, "documentSuffix", documentSuffix);
        AppendField(sb, "normalization", NormalizationPolicy);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// The identity a resolved profile describes — what the embedder verifies
    /// per poll, the guarded chunk write re-checks transactionally, the
    /// read-side guard enforces before KNN, and health/doctor/status compare.
    /// One derivation, so writers, readers and verifiers cannot disagree
    /// about what the configuration means. Always compute from the PROFILE,
    /// not OllamaOptions: suffix transforms exist only on profiles, and an
    /// options-derived hash would silently ignore them.
    /// </summary>
    public static (string SpaceId, string ConfigHash) ForProfile(ResolvedEmbeddingProfile profile) =>
        (profile.SpaceId, ComputeConfigHash(
            profile.SpaceId, profile.WireModel, profile.OutputDimensions,
            profile.QueryPrefix, profile.QuerySuffix, profile.DocumentPrefix, profile.DocumentSuffix));

    /// <summary>
    /// Legacy-shaped identity straight from <see cref="OllamaOptions"/> (no
    /// profile in play, all suffixes empty). Used by <c>SchemaMigrator</c>'s
    /// fallback when constructed without a profile, and by tests.
    /// </summary>
    public static (string SpaceId, string ConfigHash) FromOllamaOptions(OllamaOptions options)
    {
        var spaceId = LegacySpaceId(options.EmbeddingModel, options.EmbeddingDimensions);
        var hash = ComputeConfigHash(
            spaceId, options.EmbeddingModel, options.EmbeddingDimensions,
            options.QueryInstructionPrefix, querySuffix: "", documentPrefix: "", documentSuffix: "");
        return (spaceId, hash);
    }

    private static void AppendField(StringBuilder sb, string name, string value)
    {
        sb.Append(name).Append(':')
          .Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
          .Append(value).Append('\n');
    }
}
