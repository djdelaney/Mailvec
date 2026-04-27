namespace Mailvec.Core.Models;

/// <summary>
/// Per-folder summary used by `list_folders` MCP tool. OldestDate/LatestDate
/// are nullable because folders containing only messages with NULL date_sent
/// will produce NULLs from MIN/MAX.
/// </summary>
public sealed record FolderStats(
    string Folder,
    long MessageCount,
    DateTimeOffset? OldestDate,
    DateTimeOffset? LatestDate);
