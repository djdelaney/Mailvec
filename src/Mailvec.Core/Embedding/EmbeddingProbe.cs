namespace Mailvec.Core.Embedding;

public enum EmbeddingProbeStatus
{
    /// <summary>A real one-string embed succeeded — reachable AND able to serve the configured model.</summary>
    Available,

    /// <summary>Credentials or configuration rejected by the provider.</summary>
    AuthFailed,

    /// <summary>The provider answered but does not serve the configured model (Ollama: tag not pulled).</summary>
    ModelMissing,

    /// <summary>Rate limited or shedding load. NOT evidence that credentials or the model are missing.</summary>
    Backpressure,

    /// <summary>No useful answer from the provider at all.</summary>
    Unreachable,

    /// <summary>The provider answered with something structurally wrong.</summary>
    InvalidResponse,
}

/// <summary>
/// Provider-neutral readiness result, replacing the Ollama-shaped
/// PingAsync + tri-state IsModelAvailableAsync pair as the consumer-facing
/// probe (the proposal's "Provider-neutral readiness probe"). Every profile
/// answers it with a REAL embed — Ollama answers /api/tags with 200 while
/// the model can't load, so only an actual embed proves readiness.
/// <paramref name="Detail"/> is sanitized provider/model information only:
/// never an endpoint, key, or upstream body.
/// <paramref name="ModelListed"/> is the optional model-catalog diagnostic
/// (the proposal's "optional protocol/provider diagnostic, not a
/// requirement"): true/false when the provider exposes a listing that
/// answered, null otherwise. It preserves the tri-state the health report
/// has always carried — a failed probe with ModelListed=true is "model
/// pulled but can't load", which needs different remediation than either
/// "server down" or "model missing".
/// </summary>
public sealed record EmbeddingProbe(EmbeddingProbeStatus Status, string? Detail, bool? ModelListed = null)
{
    public bool IsAvailable => Status == EmbeddingProbeStatus.Available;
}
