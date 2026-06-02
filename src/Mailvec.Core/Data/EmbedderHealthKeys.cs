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
    /// can't drain its backlog. A single failed batch is not always one poll
    /// cycle: when Ollama accepts the connection but never responds (model
    /// can't load, wedged runner), each failure burns the full Polly
    /// retry+timeout budget — minutes, not 30s — before the outer catch
    /// increments this counter. 2 consecutive failures already means several
    /// minutes of sustained breakage, tight enough to surface a real stall
    /// without tripping on a single transient Ollama blip. The
    /// <see cref="StuckStaleAfter"/> backstop below catches slow-cycling
    /// failures that haven't yet reached this count.
    /// </summary>
    public const int StuckThreshold = 2;

    /// <summary>
    /// Time-based backstop for <see cref="StuckThreshold"/>. A wedged Ollama
    /// can take 15+ minutes to rack up enough consecutive failures because
    /// each cycle is dominated by the per-attempt timeout. Independently of
    /// the counter, HealthService treats the embedder as stuck when there's
    /// an unembedded backlog, the most recent attempt failed, and no batch
    /// has succeeded within this window — so a "reachable but can't embed"
    /// outage surfaces in minutes rather than depending on cycle timing.
    /// </summary>
    public static readonly TimeSpan StuckStaleAfter = TimeSpan.FromMinutes(10);
}
