namespace Mailvec.Core.Data;

/// <summary>
/// Metadata-table keys for the OCR pass's batch-outcome record. Mirrors
/// <see cref="EmbedderHealthKeys"/> deliberately: same writer/reader split
/// (the Embedder is the sole writer, HealthService and the CLI read), same
/// reason for living in Core (so the two can't drift on string names).
///
/// <para><b>Why OCR needs its own outcome record.</b> The three signals
/// <c>ServiceHeartbeat</c> documents — liveness, progress, outcome — must not
/// be collapsed, and until these keys existed OCR had only the first two:</para>
/// <list type="bullet">
/// <item>the provider probe (doctor / <c>/health</c>'s ModelAvailable) says the
/// endpoint answers, which is liveness and says nothing about whether OCR
/// produces text;</item>
/// <item>the pending counts are progress, but only by sampling twice — and
/// <b>zero is ambiguous</b>: a drained backlog and a pass that silently skips
/// everything look identical.</item>
/// </list>
/// <para>That is exactly the hole mbsync's sync marker was added to close
/// ("the beat is blind to whether the sync WORKED"). Without an outcome record
/// there is no way to answer "is OCR working right now?" on a quiet corpus
/// short of waiting for new mail to arrive.</para>
/// </summary>
public static class OcrHealthKeys
{
    /// <summary>
    /// When a document last gained text. Written on COMMITTED transitions only —
    /// a stale write-back (the row moved between selection and write) means
    /// nothing was persisted, so counting it would report progress that did not
    /// happen. <c>OcrWriteOutcome.Committed</c> already exists for this
    /// distinction; use it.
    /// </summary>
    public const string LastSuccessAt = "ocr.last_success_at";

    /// <summary>When a vision call last failed, for any reason in <see cref="Vision.VisionFailureKind"/>.</summary>
    public const string LastFailureAt = "ocr.last_failure_at";

    /// <summary>
    /// Consecutive failed cycles, reset by any committed success. A counter
    /// rather than a boolean because one failure is noise (a transient 5xx, a
    /// single poison document) and sustained failure is an outage.
    /// </summary>
    public const string ConsecutiveFailures = "ocr.consecutive_failures";

    /// <summary>
    /// The <see cref="Vision.VisionFailureKind"/> of the most recent failure.
    /// This is the field that turns "OCR isn't working" into an actionable
    /// report: <c>AuthFailed</c> sends you to the API key, <c>Backpressure</c>
    /// means it is throttled and will recover on its own, <c>DocumentFatal</c>
    /// means one document was rejected and the pass is otherwise fine.
    /// </summary>
    public const string LastFailureKind = "ocr.last_failure_kind";

    /// <summary>
    /// Documents retired to <c>extraction_status='failed'</c> by the OCR pass,
    /// cumulative. Not derivable from the attachments table, because that
    /// status is also set by the indexer's extraction path — this counts only
    /// what OCR gave up on.
    ///
    /// Worth its own counter now that <c>DocumentFatal</c> retires on the FIRST
    /// refusal rather than after five attempts: a systematic problem (every
    /// page over some size rejected) can retire many documents quickly and
    /// permanently, and nothing else would show it.
    /// </summary>
    public const string RetiredTotal = "ocr.retired_total";

    /// <summary>
    /// Pages sent to the vision provider, cumulative. A hosted provider bills
    /// per page, so this is the only gauge on what the pass is spending; an
    /// unintended re-OCR or a retry loop is otherwise invisible until the bill
    /// arrives. Meaningless-but-harmless on a local provider, where it simply
    /// counts work done.
    /// </summary>
    public const string PagesSentTotal = "ocr.pages_sent_total";

    /// <summary>
    /// How long without a committed success, while work is pending, before
    /// callers should treat OCR as stalled. Deliberately far longer than the
    /// embedder's equivalent: a single page can take tens of seconds locally,
    /// the pass yields to the embed pass between batches, and the backoff on
    /// provider backpressure is measured in cycles. Anything tighter would
    /// report a merely slow pass as broken.
    /// </summary>
    public static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(30);
}
