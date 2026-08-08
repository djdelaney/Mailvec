namespace Mailvec.Core.Embedding;

/// <summary>
/// Provider-neutral embedding failure classification — the
/// <see cref="Vision.VisionFailureKind"/> lesson applied to the embed path
/// (docs/proposals/embedding-providers.md, "Failure model and retries").
/// The distinction that must never collapse: only <see cref="Transient"/>
/// failures are evidence *against a message*; the provider-wide kinds
/// (<see cref="Backpressure"/>, <see cref="AuthOrConfig"/>,
/// <see cref="ModelUnavailable"/>) must never accrue quarantine strikes,
/// or a throttled hosted provider silently quarantines valid mail.
/// </summary>
public enum EmbeddingFailureKind
{
    /// <summary>Credentials or configuration rejected (hosted 401/403). No retry; fail clearly.</summary>
    AuthOrConfig,

    /// <summary>The provider answered but cannot serve the configured model (404, model errors). No blind retry.</summary>
    ModelUnavailable,

    /// <summary>Rate limiting or load shedding (429/503). The next poll IS the backoff; never message evidence.</summary>
    Backpressure,

    /// <summary>Positively identified context overflow. Only this kind may trigger the split/truncate fallback.</summary>
    InputTooLong,

    /// <summary>The provider returned something structurally wrong (count, index, dimension, parse). Fail loudly.</summary>
    InvalidResponse,

    /// <summary>Network faults, timeouts, other 5xx — bounded retry, and the only kind that may count strikes.</summary>
    Transient,

    /// <summary>
    /// The active profile describes a different vector space than the one the
    /// stored vectors belong to (model, dimensions, space id, or config
    /// hash). Raised by the read-side guard before query embedding/KNN:
    /// serving a cross-space ranking would be plausible and meaningless.
    /// Configuration-level, never message evidence; remedied by reverting
    /// config or `mailvec switch-model`.
    /// </summary>
    SpaceMismatch,
}

/// <summary>
/// Carries the classification without carrying the upstream body: provider
/// error bodies can echo their input, and the input is mail content, so the
/// message here is always Mailvec-authored (status codes and counts only).
/// An unclassified provider failure must map to <see cref="EmbeddingFailureKind.Transient"/>
/// — a provider that forgets to classify degrades to "retry it", never
/// "destroy it".
/// </summary>
public sealed class EmbeddingException(EmbeddingFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public EmbeddingFailureKind Kind { get; } = kind;

    /// <summary>Provider-wide conditions: true means this failure says nothing about any particular message.</summary>
    public bool IsProviderWide =>
        Kind is EmbeddingFailureKind.AuthOrConfig
             or EmbeddingFailureKind.ModelUnavailable
             or EmbeddingFailureKind.Backpressure
             or EmbeddingFailureKind.SpaceMismatch;
}
