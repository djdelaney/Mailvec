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

    /// <summary>
    /// Compact, stable identity of the engine behind this client, recorded on
    /// every row it OCRs (<c>attachments.ocr_model</c>) so a later provider
    /// switch can tell which documents came from which engine — the thing
    /// <c>mailvec reocr</c> could not previously select on.
    ///
    /// <para><b>Asked of the client, not derived from config, on purpose.</b>
    /// The value has to describe the engine that actually produced the text. A
    /// second read of <c>Vision:Provider</c> at write time is a different fact
    /// from "what the object I just called is", and the two diverge exactly
    /// when it matters — a config reload, or a process holding a client built
    /// before the change.</para>
    ///
    /// <para><b>Shape is <c>provider:model</c>, deliberately NOT
    /// <see cref="VisionRegistration.Describe"/></b>, which appends the hosted
    /// endpoint URL. This string is written to thousands of rows and is a
    /// candidate for the MCP surface; an endpoint is deployment state rather
    /// than engine identity, and baking it into the corpus would both bloat it
    /// and leak an internal address anywhere the column is surfaced.</para>
    ///
    /// <para>The default keeps hand-built test fakes compiling and is honest
    /// about what it knows; both real clients override it.</para>
    /// </summary>
    string ModelId => "unknown";
}
