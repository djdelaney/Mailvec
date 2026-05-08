namespace Mailvec.Core.Options;

public sealed class IndexerOptions
{
    public const string SectionName = "Indexer";

    public int ScanIntervalSeconds { get; set; } = 300;
    public int DebounceMilliseconds { get; set; } = 500;
    public int MaxHtmlBodyBytes { get; set; } = 1_048_576;

    // Cap on per-attachment bytes for text extraction. Anything larger is
    // stamped extraction_status='oversize' and skipped — protects the indexer
    // from a 200MB PDF blowing up memory + CPU during parse. 25MB covers
    // typical statements, contracts, and scanned legal docs without dragging
    // in genuinely huge files (image-heavy PDFs, raw datasets).
    public long AttachmentMaxBytes { get; set; } = 25 * 1024 * 1024;
}
