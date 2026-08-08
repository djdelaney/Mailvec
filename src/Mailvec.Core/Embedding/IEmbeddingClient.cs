namespace Mailvec.Core.Embedding;

/// <summary>
/// Provider-neutral embedding seam. OllamaClient is the only implementation
/// today; a hosted API (OpenAI, Voyage) would slot in here without touching
/// consumers. Contract: <see cref="EmbedAsync"/> returns one float[] per
/// input, in the same order, RAW — the mathematical contract (dimension
/// width, finiteness, L2 normalization for vec0's L2 KNN) is enforced once
/// by EmbeddingService for every transport. This interface is the protocol
/// transport seam; consumers go through IEmbeddingService.
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>
    /// Embed each input string. Returns one RAW float[] per input, same
    /// order — dimension validation, finiteness checks and L2 normalization
    /// are owned by EmbeddingService (once, for every transport), not here.
    /// May truncate over-long inputs rather than fail; throws a classified
    /// EmbeddingException on non-recoverable provider errors.
    /// </summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default);

    /// <summary>
    /// Readiness check — true only when the provider is reachable AND can
    /// actually produce an embedding with the configured model. Bounded by a
    /// short internal timeout; returns false on any error.
    /// </summary>
    Task<bool> PingAsync(CancellationToken ct = default);

    /// <summary>
    /// Diagnostic follow-up for a failed <see cref="PingAsync"/>: is the
    /// configured embedding model installed on the provider? Tri-state —
    /// true/false when the provider answered (model present/absent), null
    /// when the provider itself was unreachable. Health/doctor use this to
    /// tell "server down" apart from "server up but model not pulled", which
    /// need opposite remediation. Bounded by a short internal timeout.
    /// </summary>
    Task<bool?> IsModelAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Content digest of the model ARTIFACT serving embeddings, when the
    /// provider makes one observable (Ollama tags resolve to manifest
    /// digests). This is the local half of the stability hybrid
    /// (docs/proposals/embedding-providers.md, decision 2): the embedder
    /// records it and refuses when it changes — a re-pulled tag with
    /// different weights is a new vector space wearing the old name.
    /// Null means "not observable right now" (provider unreachable, digest
    /// not exposed, or a hosted profile whose weights are opaque) and must
    /// NEVER be treated as a mismatch — unknown is not drift, the same rule
    /// the heartbeats follow. The default implementation returns null so
    /// hosted transports (whose check is sentinel-based instead) and test
    /// fakes are correct by default.
    /// </summary>
    Task<string?> GetModelArtifactDigestAsync(CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}
