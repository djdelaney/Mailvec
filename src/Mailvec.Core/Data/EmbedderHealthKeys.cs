namespace Mailvec.Core.Data;

/// <summary>
/// Metadata-table keys for the embedder's batch-outcome heartbeat. The
/// Embedder process (sole writer) updates these on every attempted batch;
/// the MCP server's HealthService reads them to surface "is the embedder
/// stuck" through /health without needing an IPC channel between the two
/// processes. The constants live here in Core so the writer and reader
/// can't drift on string names.
/// </summary>
public static class EmbedderHealthKeys
{
    public const string LastSuccessAt = "embedder.last_success_at";
    public const string LastFailureAt = "embedder.last_failure_at";
    public const string ConsecutiveFailures = "embedder.consecutive_failures";
    public const string LastFailureKind = "embedder.last_failure_kind";

    /// <summary>
    /// Threshold at which /health flips to "degraded" because the embedder
    /// can't drain its backlog. Each consecutive failure roughly equals one
    /// poll cycle (default 30s in EmbedderOptions), so 3 ≈ 90s of sustained
    /// breakage before paging — generous enough to absorb a transient Ollama
    /// blip without false-positives, tight enough that a true stuck state
    /// surfaces within a couple of minutes rather than the 3-day window the
    /// orphan-vector bug previously slipped through.
    /// </summary>
    public const int StuckThreshold = 3;
}
