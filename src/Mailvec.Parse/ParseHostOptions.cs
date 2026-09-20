namespace Mailvec.Parse;

/// <summary>
/// The host's own settings, bound from the same <c>Parser:*</c> section the
/// callers' <c>ParserOptions</c> lives in (different keys, deliberately a
/// different class: this project never references Core). Env vars in compose:
/// <c>Parser__RequestTimeoutSeconds</c>, <c>Parser__MaxRequestsBeforeExit</c>,
/// <c>Parser__MaxRequestBodyBytes</c>, <c>Parser__BindAddress</c>, <c>Parser__Port</c>.
/// </summary>
public sealed class ParseHostOptions
{
    public const string SectionName = "Parser";

    /// <summary>
    /// The attachment-text extractor's size gate, read from
    /// <c>Indexer:AttachmentMaxBytes</c> so the parse container agrees with
    /// the indexer about what "oversize" means. The default mirrors
    /// <c>IndexerOptions.AttachmentMaxBytes</c> (25 MB) — duplicated here
    /// because that class lives in Core.
    /// </summary>
    public const long DefaultAttachmentMaxBytes = 25L * 1024 * 1024;

    public string BindAddress { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 3400;

    /// <summary>
    /// Ceiling on one parse. PDFium, PdfPig and OpenXml take no cancellation
    /// token, so an overrunning parse cannot be stopped, only abandoned: on
    /// timeout the host answers 504 <b>and exits</b> (compose restarts it in
    /// seconds). Phase 0 measured legitimate renders at 25–750 ms and the first
    /// hostile one at 69 s; a too-low value turns big-but-honest scans into
    /// permanent failures, so err high.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Exit cleanly after this many requests (0 = never). A compromised
    /// process then persists for at most this many documents — the one control
    /// here that bounds persistence rather than reach.
    /// </summary>
    public int MaxRequestsBeforeExit { get; set; } = 500;

    /// <summary>
    /// Kestrel's request body cap. A whole <c>.eml</c> carrying a 25 MB
    /// attachment base64-inflates to ~34 MB; 48 MB leaves headroom.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = 48L * 1024 * 1024;

    /// <summary>
    /// How many parses may run at once. Kestrel accepts every connection and
    /// each parse decodes a whole message (PdfPig and OpenXml peaks are the
    /// reason this container has its own <c>mem_limit</c>), so without a gate
    /// a bulk-ingest scan, an OCR render batch and a tool call could hold
    /// several decoded documents at once and be OOM-killed together — a
    /// <c>Crashed</c> strike against every innocent document in flight. A
    /// request that cannot take the gate within <see cref="SlotWaitSeconds"/>
    /// is answered 503 (<c>Unavailable</c> to the caller: wait, not a strike).
    /// The three callers run one parse each in steady state; 4 leaves room.
    /// </summary>
    public int MaxConcurrentParses { get; set; } = 4;

    /// <summary>
    /// How long a request waits for a parse slot before it is answered 503.
    /// Short on purpose, and separate from the parse timeout: a caller told
    /// "busy" retries on its own schedule (the CLI backfills probe and
    /// retry, the scanner and OCR pass on their next tick), whereas a caller
    /// queued for a whole parse timeout and then parsing for another holds
    /// its connection for twice the budget. The host's worst case per request
    /// is therefore this plus <see cref="RequestTimeoutSeconds"/>, and the
    /// caller's <c>Parser:RequestTimeoutSeconds</c> must exceed that sum.
    /// </summary>
    public int SlotWaitSeconds { get; set; } = 10;

    public TimeSpan SlotWait => TimeSpan.FromSeconds(Math.Max(1, SlotWaitSeconds));

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(Math.Max(1, RequestTimeoutSeconds));
}
