namespace Mailvec.Core.Options;

public sealed class IndexerOptions
{
    public const string SectionName = "Indexer";

    public int ScanIntervalSeconds { get; set; } = 300;
    public int DebounceMilliseconds { get; set; } = 500;
    // NOTE: there is deliberately no MaxHtmlBodyBytes option. The HTML input
    // cap (and the DOM recursion depth cap) are constants inside HtmlToText —
    // crash-safety bounds, not tuning knobs — and they must apply identically
    // to the indexer and `mailvec rebuild-bodies`. The option that used to
    // sit here was never wired to anything.

    // Cap on per-attachment bytes for text extraction. Anything larger is
    // stamped extraction_status='oversize' and skipped — protects the indexer
    // from a 200MB PDF blowing up memory + CPU during parse. 25MB covers
    // typical statements, contracts, and scanned legal docs without dragging
    // in genuinely huge files (image-heavy PDFs, raw datasets).
    public long AttachmentMaxBytes { get; set; } = 25 * 1024 * 1024;

    // Mass-deletion hold (MaildirScanner). A single scan that would
    // soft-delete more than MassDeletionFraction of the tracked paths AND at
    // least MassDeletionMinimum messages is held rather than applied, and
    // re-evaluated every scan; it proceeds once it has persisted for
    // MassDeletionHoldMinutes. A partly present Maildir (a folder mid-move or
    // re-download on the server, a half-mounted volume, a partial restore)
    // looks exactly like the user deleting that mail, and the only earlier
    // guard was "the walk saw zero files". Soft-deletes are recoverable (rows
    // resurrect when their files reappear); `purge-deleted` is not, so the
    // hold is what keeps a transient outage from ever reaching it. A genuine
    // bulk deletion still lands, an hour late. Set the fraction to 1 or the
    // hold to 0 to disable. The day-to-day deletion rate is tens of messages.
    public double MassDeletionFraction { get; set; } = 0.10;
    public int MassDeletionMinimum { get; set; } = 500;
    public int MassDeletionHoldMinutes { get; set; } = 60;
}
