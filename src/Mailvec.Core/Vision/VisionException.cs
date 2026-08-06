namespace Mailvec.Core.Vision;

/// <summary>
/// Why a vision call failed — the discriminator the OCR pass branches on.
///
/// This exists because the OCR pass's original design had exactly two failure
/// classes ("this document is poison" vs "the provider is down"), told apart by
/// whether anything else succeeded in the same cycle. That inference is sound
/// for a local Ollama, where the only ways to fail are a bad document or a dead
/// model. It is NOT sound for a hosted provider, which can also refuse a
/// perfectly good document for reasons that say nothing about it.
///
/// The dangerous case is <see cref="Backpressure"/>. A rate-limited call
/// arrives as an ordinary exception, and if other documents in the same cycle
/// happened to succeed, the pass concludes the model is healthy and counts the
/// failure as a strike. Five throttled cycles and a good scan is stamped
/// 'failed' — permanently, because no query ever re-selects a failed row.
/// A traffic spike would quietly burn documents.
/// </summary>
public enum VisionFailureKind
{
    /// <summary>
    /// Might work next time and might not — a timeout, a dropped connection, a
    /// 5xx. The historical default, and still the only kind that can accrue
    /// strikes toward retiring a poison document.
    /// </summary>
    Transient,

    /// <summary>
    /// The provider is asking us to slow down (HTTP 429, or a 503 carrying
    /// Retry-After). Says nothing about the document. **Must never count toward
    /// retirement**, and should stop the current batch rather than hammering
    /// the next candidate into the same wall.
    /// </summary>
    Backpressure,

    /// <summary>
    /// Credentials, endpoint, deployment name, or quota configuration is wrong
    /// (401, 403, 404 on the route). Every call will fail identically until a
    /// human fixes it, so retiring documents would destroy the queue for a
    /// reason that has nothing to do with any of them. Abort the batch loudly
    /// and leave everything selectable.
    /// </summary>
    AuthOrConfig,

    /// <summary>
    /// The provider looked at THIS payload and refused it — too large, malformed,
    /// unsupported (413, 415, and 400s that are about the document rather than
    /// the request envelope). Deterministic for this document, so it retires
    /// immediately rather than burning <c>MaxVisionAttempts</c> cycles proving
    /// it. Same treatment the pass already gives a PDF PDFium cannot open.
    /// </summary>
    DocumentFatal,
}

/// <summary>
/// A vision-provider failure carrying its <see cref="VisionFailureKind"/>.
/// Both <c>OllamaVisionClient</c> and <c>MistralOcrClient</c> throw this so the
/// OCR pass never has to guess a cause from an exception type.
///
/// Any other exception reaching the pass is treated as
/// <see cref="VisionFailureKind.Transient"/> — the historical, conservative
/// default. That fallback is deliberate: a new provider that forgets to
/// classify something degrades to "retry it", never to "destroy it".
/// </summary>
public sealed class VisionException(VisionFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public VisionFailureKind Kind { get; } = kind;
}
