namespace Mailvec.Core.Embedding;

public static class VectorMath
{
    /// <summary>
    /// L2-normalizes <paramref name="vec"/> in place — unless it is already
    /// normalized (within <paramref name="epsilon"/> of unit norm) or is the
    /// zero vector, in which case it is left bit-for-bit untouched. The
    /// untouched fast path matters: mxbai-embed-large vectors arrive
    /// normalized from Ollama, and skipping the divide keeps re-embedded
    /// vectors byte-identical to ones stored before this safeguard existed.
    /// The safeguard itself exists because vec0 KNN uses L2 distance, which
    /// only ranks like cosine similarity when every stored vector (and the
    /// query vector) has unit norm — a future model that returns raw
    /// unnormalized embeddings would otherwise silently skew ranking toward
    /// short vectors.
    /// </summary>
    public static void NormalizeInPlaceIfNeeded(float[] vec, float epsilon = 1e-3f)
    {
        ArgumentNullException.ThrowIfNull(vec);

        double sumSquares = 0d;
        for (int i = 0; i < vec.Length; i++) sumSquares += (double)vec[i] * vec[i];

        var norm = Math.Sqrt(sumSquares);
        if (norm == 0d || Math.Abs(norm - 1d) <= epsilon) return;

        var inv = (float)(1d / norm);
        for (int i = 0; i < vec.Length; i++) vec[i] *= inv;
    }
}
