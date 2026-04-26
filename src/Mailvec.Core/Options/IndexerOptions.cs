namespace Mailvec.Core.Options;

public sealed class IndexerOptions
{
    public const string SectionName = "Indexer";

    public int ScanIntervalSeconds { get; set; } = 300;
    public int DebounceMilliseconds { get; set; } = 500;
    public int MaxHtmlBodyBytes { get; set; } = 1_048_576;
}
