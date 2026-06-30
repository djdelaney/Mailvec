namespace Mailvec.Core.Vision;

/// <summary>
/// Provider-neutral seam for vision OCR. <see cref="Ollama.OllamaVisionClient"/>
/// is the only implementation today. Used by the embedder's scanned-PDF OCR
/// pass to turn a rendered page image into searchable text. Separate from
/// <c>IEmbeddingClient</c> — OCR is a generate call, not an embed.
/// </summary>
public interface IVisionClient
{
    /// <summary>
    /// Transcribe all text from a single page image (JPEG/PNG bytes). Returns
    /// the recovered text, possibly empty. Throws on a non-recoverable provider
    /// error so the caller can log and leave the attachment unprocessed
    /// (to be retried on a later pass).
    /// </summary>
    Task<string> OcrAsync(byte[] image, CancellationToken ct = default);

    /// <summary>
    /// True iff the configured vision model is pulled and the provider is
    /// reachable. Bounded by a short internal timeout; returns false on any
    /// error. Lets the embedder skip OCR gracefully — and <c>mailvec doctor</c>
    /// warn — when the model hasn't been pulled.
    /// </summary>
    Task<bool> IsModelAvailableAsync(CancellationToken ct = default);
}
