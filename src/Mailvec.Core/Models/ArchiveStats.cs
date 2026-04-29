namespace Mailvec.Core.Models;

/// <summary>
/// Whole-archive summary surfaced on every search response so Claude can see
/// the scope it's searching against. A 10-year archive with hundreds of
/// thousands of messages should prompt time-bounded queries; a tiny corpus
/// makes unbounded queries fine. OldestDate/LatestDate are nullable because
/// an empty archive (or one whose messages all have NULL date_sent) produces
/// NULLs from MIN/MAX.
/// </summary>
public sealed record ArchiveStats(
    long TotalMessages,
    DateTimeOffset? OldestDate,
    DateTimeOffset? LatestDate);
