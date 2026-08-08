namespace Mailvec.Core.Embedding;

/// <summary>
/// The purpose-aware embedding seam every consumer goes through
/// (docs/proposals/embedding-providers.md, "Service and transport
/// boundaries"). Queries and documents are DIFFERENT purposes:
/// instruction-tuned models are trained asymmetrically, and the profile's
/// query/document transforms are applied here, centrally — callers can no
/// longer issue an untyped embed that bypasses text policy, which is how a
/// prefix once applied in one call site and forgotten in another would
/// split the vector space. <see cref="IEmbeddingTransport"/> remains
/// underneath as the protocol transport; consumers should not touch it.
/// The transforms applied here are part of the mathematical embedding
/// space — they are covered by the config hash, and changing one requires
/// a full re-embed.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>Embed one search query, with the profile's query transform applied. Returns an L2-normalized vector.</summary>
    Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default);

    /// <summary>Embed document/attachment chunks, with the profile's document transform applied. One L2-normalized vector per input, in order.</summary>
    Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default);

    /// <summary>Provider-neutral readiness: a real one-string embed, classified. Bounded internally to fit the /health budget.</summary>
    Task<EmbeddingProbe> ProbeAsync(CancellationToken ct = default);

    /// <summary>Artifact digest passthrough (stability hybrid, local half). Null = unobservable, never drift.</summary>
    Task<string?> GetModelArtifactDigestAsync(CancellationToken ct = default);
}
