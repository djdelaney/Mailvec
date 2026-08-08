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
    /// Canonical, versioned config hash. Field values are length-prefixed so
    /// no delimiter collision can make two different configurations serialize
    /// identically; the leading <c>v1</c> versions the serialization itself
    /// (adding a field later means bumping it, which correctly invalidates
    /// every stored hash rather than silently comparing across shapes).
    /// </summary>
    public static string ComputeConfigHash(
        string spaceId, string wireModel, int dimensions, string queryPrefix, string documentPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(wireModel);

        var sb = new StringBuilder();
        sb.Append("v1\n");
        AppendField(sb, "spaceId", spaceId);
        AppendField(sb, "wireModel", wireModel);
        AppendField(sb, "dimensions", dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendField(sb, "queryPrefix", queryPrefix);
        AppendField(sb, "documentPrefix", documentPrefix);
        AppendField(sb, "normalization", NormalizationPolicy);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// The identity the current Ollama configuration describes. This is what
    /// the embedder verifies per poll, what the guarded chunk write re-checks
    /// transactionally, and what <c>switch-model</c> stamps — one derivation,
    /// so the writers and the verifiers cannot disagree about what the config
    /// means. Document prefix is empty today (no configured document
    /// transform exists yet); when one arrives it must be threaded through
    /// here, never appended at a call site.
    /// </summary>
    public static (string SpaceId, string ConfigHash) FromOllamaOptions(OllamaOptions options)
    {
        var spaceId = LegacySpaceId(options.EmbeddingModel, options.EmbeddingDimensions);
        var hash = ComputeConfigHash(
            spaceId, options.EmbeddingModel, options.EmbeddingDimensions,
            options.QueryInstructionPrefix, documentPrefix: "");
        return (spaceId, hash);
    }

    private static void AppendField(StringBuilder sb, string name, string value)
    {
        sb.Append(name).Append(':')
          .Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':')
          .Append(value).Append('\n');
    }
}
