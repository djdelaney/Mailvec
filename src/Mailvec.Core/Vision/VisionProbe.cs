namespace Mailvec.Core.Vision;

/// <summary>
/// Outcome of a vision-provider health probe, at the granularity an operator
/// needs to act on.
/// </summary>
public enum VisionProbeStatus
{
    /// <summary>Provider reachable, credentials accepted, model usable.</summary>
    Available,

    /// <summary>
    /// Ollama answered but the configured vision model isn't pulled.
    /// Remedy is <c>ollama pull</c>; nothing is wrong with the connection.
    /// </summary>
    ModelMissing,

    /// <summary>No answer at all — wrong host, firewall, service down.</summary>
    Unreachable,

    /// <summary>The endpoint rejected our credentials (401/403). The key is wrong or revoked.</summary>
    AuthFailed,

    /// <summary>
    /// The endpoint answered but the route/deployment doesn't exist (404).
    /// On Azure AI Foundry this is usually a wrong route or deployment name,
    /// not a wrong key — a distinction worth keeping, because the two send you
    /// to completely different settings.
    /// </summary>
    RouteNotFound,

    /// <summary>
    /// A hosted provider is configured but THIS process holds no credentials —
    /// the intended posture for the MCP server and CLI, where the key is
    /// deliberately scoped to the embedder. Not a fault, and specifically not
    /// the same as "unavailable": reporting it as a failure would train the
    /// operator to ignore the indicator.
    /// </summary>
    NotConfiguredHere,
}

/// <summary>Probe result plus an optional human detail (status code, message).</summary>
public sealed record VisionProbe(VisionProbeStatus Status, string? Detail)
{
    public bool IsAvailable => Status == VisionProbeStatus.Available;

    /// <summary>
    /// Whether this outcome means OCR is actually broken. A provider that
    /// simply isn't checkable from this process is not.
    /// </summary>
    public bool IsFault => Status is not (VisionProbeStatus.Available or VisionProbeStatus.NotConfiguredHere);
}
