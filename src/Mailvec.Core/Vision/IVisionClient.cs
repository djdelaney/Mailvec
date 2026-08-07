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
    /// Transcribe all text from a single *scanned-document page* image (JPEG/PNG
    /// bytes). Returns the recovered text, possibly empty. Throws on a
    /// non-recoverable provider error so the caller can log and leave the
    /// attachment unprocessed (to be retried on a later pass). Uses the
    /// document-oriented prompt — assumes the image is a page that contains text.
    /// </summary>
    Task<string> OcrAsync(byte[] image, CancellationToken ct = default);

    /// <summary>
    /// Transcribe text from an arbitrary *image attachment* (photo, screenshot,
    /// diagram), which — unlike a scanned page — may legitimately contain no
    /// text at all. Uses a prompt with an explicit "output nothing if there's no
    /// text" escape hatch to suppress the hallucinated single words the
    /// document prompt produces on textless photos. Otherwise identical to
    /// <see cref="OcrAsync"/>.
    /// </summary>
    Task<string> OcrImageAsync(byte[] image, CancellationToken ct = default);

    /// <summary>
    /// True iff the configured vision model is pulled and the provider is
    /// reachable. Bounded by a short internal timeout; returns false on any
    /// error. Lets the embedder skip OCR gracefully — and <c>mailvec doctor</c>
    /// warn — when the model hasn't been pulled.
    /// </summary>
    Task<bool> IsModelAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// The same check, but saying WHY. A bool collapses "the key is wrong",
    /// "the endpoint is unreachable", "this process holds no credentials by
    /// design" and "the model isn't pulled" into one indistinguishable false,
    /// which is the difference between an operator fixing the problem in a
    /// minute and reading container logs for twenty.
    ///
    /// Default implementation degrades to the bool so an existing
    /// <see cref="IVisionClient"/> (or a test fake) keeps working; the real
    /// clients override it with the detail they actually have.
    /// </summary>
    async Task<VisionProbe> ProbeAsync(CancellationToken ct = default) =>
        await IsModelAvailableAsync(ct).ConfigureAwait(false)
            ? new VisionProbe(VisionProbeStatus.Available, null)
            : new VisionProbe(VisionProbeStatus.Unreachable, null);
}
