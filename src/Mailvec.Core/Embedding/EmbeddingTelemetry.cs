using Microsoft.Extensions.Logging;

namespace Mailvec.Core.Embedding;

/// <summary>
/// Provider-neutral per-request observations from a hosted embeddings call.
/// EVERY field is optional — the proposal's rule is that missing telemetry
/// is not an invalid response, so absence must be representable, never an
/// error. Carries usage numbers, the provider's echoed model, its request
/// id, and rate-limit headroom; never mail content, never credentials.
/// </summary>
public sealed record EmbeddingTelemetry(
    int? PromptTokens,
    string? ResponseModel,
    string? RequestId,
    long? RateLimitRemainingRequests,
    long? RateLimitRemainingTokens);

/// <summary>
/// Receives telemetry from hosted transports. Kept as an interface (not an
/// event) so registration decides the sink once per process; the default
/// sink logs at Debug — enough for the phase-6 cost/throttling audit to be
/// grepped out of the embedder log without a metrics stack.
/// </summary>
public interface IEmbeddingTelemetryObserver
{
    void OnEmbeddingResponse(EmbeddingTelemetry telemetry);
}

/// <summary>Debug-level logging sink; the audit greps these lines.</summary>
public sealed class LoggingEmbeddingTelemetryObserver(ILogger<LoggingEmbeddingTelemetryObserver> logger)
    : IEmbeddingTelemetryObserver
{
    public void OnEmbeddingResponse(EmbeddingTelemetry t) =>
        logger.LogDebug(
            "Embedding telemetry: promptTokens={PromptTokens} model={ResponseModel} requestId={RequestId} " +
            "rlRemainingRequests={RlRequests} rlRemainingTokens={RlTokens}",
            t.PromptTokens, t.ResponseModel, t.RequestId,
            t.RateLimitRemainingRequests, t.RateLimitRemainingTokens);
}
