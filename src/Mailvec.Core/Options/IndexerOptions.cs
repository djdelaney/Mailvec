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
}
