using System.Text.Json.Serialization;

namespace Mailvec.Core.Tray;

/// <summary>
/// Payload returned by the MCP server's <c>/tray/status</c> endpoint. Field
/// names are camelCase on the wire (System.Text.Json default for these
/// records) so the Swift tray app can decode them with stock JSONDecoder. This
/// is the contract the Mailvec.Tray Swift target depends on — renames here
/// require lockstep changes in <c>Models.swift</c>.
/// </summary>
public sealed record TrayStatus(
    string Severity,           // "ok" | "syncing" | "warn" | "error"
    long Messages,             // live message count (excludes deleted)
    long Deleted,
    long Embedded,
    long EmbedTotal,
    long Chunks,
    DateTimeOffset? LastIndexedAt,
    DateTimeOffset? LastSyncAt,
    long DbSizeBytes,
    string SchemaVersion,
    IReadOnlyList<TrayServiceStatus> Services,
    TrayOllamaStatus Ollama,
    TrayEmbedProgress? Progress,
    IReadOnlyList<TrayTimelineEvent> RecentEvents,
    IReadOnlyList<int> Sparkline);  // 30 buckets of embeddings/min, oldest first

public sealed record TrayServiceStatus(
    string Id,                 // "mbsync" | "indexer" | "embedder" | "mcp"
    string Detail,             // human caption for the tile
    bool Ok,
    bool Busy,
    string? Severity);         // optional override of derived severity

public sealed record TrayOllamaStatus(
    bool Ok,
    string Detail,
    string? Severity);

public sealed record TrayEmbedProgress(
    long Done,
    long Total,
    int RatePerMinute,
    int EtaMinutes);

public sealed record TrayTimelineEvent(
    DateTimeOffset Time,
    string Kind,               // "sync" | "indexed" | "embed" | "error"
    string Text,
    string Agent,
    bool Live,
    string? Severity);

/// <summary>
/// Read-only system snapshot for the Preferences window. Most fields come
/// from existing config + a few SQLite reads; the launchd-derived bits reuse
/// <see cref="LaunchdInspector"/> output.
/// </summary>
public sealed record TraySystem(
    string MaildirRoot,
    string MbsyncrcPath,
    string MbsyncSchedule,
    string ImapHost,
    string ImapUser,
    string? LastSyncRelative,
    string? LastSyncDetail,
    string? NextSyncRelative,

    string DbPath,
    string DbSize,
    string SchemaVersion,
    string VecDylibVersion,

    string OllamaEndpoint,
    bool OllamaReachable,
    int OllamaPingMs,
    string EmbeddingModel,
    int ModelDimensions,
    bool SchemaModelMatches,
    long CoverageDone,
    long CoverageTotal,

    bool McpHttpEnabled,
    string McpBindAddress,
    int McpPort,
    bool McpbInstalled,
    string? McpbVersion,
    string AttachmentDownloadDir,

    long SoftDeletedCount);

/// <summary>
/// Flat search-hit shape for the tray. Mirrors the existing MCP
/// <c>search_emails</c> response one-for-one (id / messageId / folder /
/// from / subject / snippet / scores / matchedAttachment / webmailUrl) so we
/// don't grow a third spelling. The Swift side decodes this directly.
/// </summary>
public sealed record TraySearchHit(
    long Id,
    string MessageId,
    string Folder,
    string? Subject,
    string? FromAddress,
    string? FromName,
    DateTimeOffset? DateSent,
    string Snippet,
    double Score,              // unified ranking score (RRF, BM25, or 1/(1+dist))
    double? Bm25Score,
    double? VectorScore,       // 1 - distance (vector) so higher = better
    TrayMatchedAttachment? MatchedAttachment,
    string? WebmailUrl);

public sealed record TrayMatchedAttachment(int PartIndex, string? FileName, string? SizeHint);

public sealed record TraySearchResponse(
    string? Query,
    string Mode,
    int Count,
    IReadOnlyList<TraySearchHit> Results);

public sealed record TraySearchRequest(
    string? Query,
    string? Mode,
    int? Limit,
    string? Folder,
    string? DateFrom,
    string? DateTo,
    string? FromContains,
    string? FromExact);

public sealed record TrayControlRequest(string Service, string Action);

public sealed record TrayControlResponse(bool Ok, string Detail);

/// <summary>
/// Echo shape for the <c>POST /tray/attachment</c> endpoint. Returns the path
/// the file was written to so the tray app can call NSWorkspace.open(...).
/// </summary>
public sealed record TrayAttachmentRequest(long MessageId, int PartIndex);

public sealed record TrayAttachmentResponse(
    string Path,
    long Bytes,
    string ContentType,
    bool WasReused);

/// <summary>
/// Full-message payload for the inline preview the tray's search popover
/// expands when a row is selected. Returns the plain-text body so we don't
/// have to render HTML inside the popover; if a message has no plaintext
/// (HTML-only with no extracted text), bodyText will be null and the tray
/// shows a placeholder + "Open in Fastmail" fallback.
/// </summary>
public sealed record TrayEmail(
    long Id,
    string MessageId,
    string Folder,
    string? Subject,
    string? FromAddress,
    string? FromName,
    string? To,
    DateTimeOffset? DateSent,
    string? BodyText,
    bool HasHtml,
    IReadOnlyList<TrayEmailAttachment> Attachments,
    string? WebmailUrl);

public sealed record TrayEmailAttachment(
    int PartIndex,
    string? FileName,
    string ContentType,
    long Size);
