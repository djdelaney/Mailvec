namespace Mailvec.Core.Embedding;

/// <summary>
/// Provider-neutral embedding seam. OllamaClient is the only implementation
/// today; a hosted API (OpenAI, Voyage) would slot in here without touching
/// consumers. Contract: <see cref="EmbedAsync"/> returns one float[] per
/// input, in the same order, and every vector is L2-normalized — vec0 KNN
/// uses L2 distance, so normalization is what makes ranking
/// cosine-equivalent across models. Implementations must validate vector
/// length against the configured dimension count before returning.
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>
    /// Embed each input string. Returns one L2-normalized float[] per input,
    /// same order. May truncate over-long inputs rather than fail; throws on
    /// non-recoverable provider errors.
    /// </summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default);

    /// <summary>
    /// Readiness check — true only when the provider is reachable AND can
    /// actually produce an embedding with the configured model. Bounded by a
    /// short internal timeout; returns false on any error.
    /// </summary>
    Task<bool> PingAsync(CancellationToken ct = default);
}
