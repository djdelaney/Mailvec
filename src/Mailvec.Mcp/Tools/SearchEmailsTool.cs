using System.ComponentModel;
using System.Globalization;
using Mailvec.Core.Options;
using Mailvec.Core.Search;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Mailvec.Mcp.Tools;

/// <summary>
/// MCP wrapper around the Core search services. Returns both the internal
/// SQLite id and the RFC Message-ID for each hit so callers can choose which
/// to use for follow-up lookups (deep-linking back to Fastmail's web UI would
/// need a JMAP Email/get mapping; not implemented yet).
/// </summary>
[McpServerToolType]
public sealed class SearchEmailsTool(
    KeywordSearchService keyword,
    VectorSearchService vector,
    HybridSearchService hybrid,
    IOptions<McpOptions> mcpOptions)
{
    private readonly McpOptions _mcp = mcpOptions.Value;

    [McpServerTool(Name = "search_emails")]
    [Description(
        "Search the local email archive. Defaults to hybrid (keyword + semantic with " +
        "reciprocal rank fusion). Returns the most relevant messages, each with its " +
        "internal id, RFC message_id header, folder, sender, date, snippet, and a " +
        "score breakdown. Use the returned id with future get_email/get_thread tools.")]
    public async Task<SearchEmailsResponse> SearchEmails(
        [Description("Free-text query. For mode=keyword this is an FTS5 expression (supports phrase quotes, AND/OR/NOT). For mode=semantic/hybrid it's natural language.")]
        string query,
        [Description("Search mode: 'hybrid' (default), 'keyword' (BM25 only), or 'semantic' (vector only).")]
        string mode = "hybrid",
        [Description("Max number of results to return. Server caps this at the configured SearchMaxLimit.")]
        int? limit = null,
        [Description("Restrict to a single folder, exact match (e.g. 'INBOX', 'Archive.2024').")]
        string? folder = null,
        [Description("Earliest message date (inclusive), ISO 8601, e.g. '2024-01-01' or '2024-01-01T00:00:00Z'.")]
        string? dateFrom = null,
        [Description("Latest message date (inclusive), ISO 8601.")]
        string? dateTo = null,
        [Description("Case-insensitive substring against from_address OR from_name. Useful when only the sender's name or domain is known.")]
        string? fromContains = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new McpException("query is required.");

        var resolvedLimit = ClampLimit(limit);
        var filters = BuildFilters(folder, dateFrom, dateTo, fromContains);
        var resolvedMode = NormaliseMode(mode);

        IReadOnlyList<EmailHit> hits = resolvedMode switch
        {
            "keyword" => keyword.Search(query, resolvedLimit, filters).Select(EmailHit.FromKeyword).ToList(),
            "semantic" => (await vector.SearchAsync(query, resolvedLimit, k: Math.Max(100, resolvedLimit * 5), filters, ct).ConfigureAwait(false))
                .Select(EmailHit.FromVector).ToList(),
            "hybrid" => (await hybrid.SearchAsync(query, resolvedLimit, filters: filters, ct: ct).ConfigureAwait(false))
                .Select(EmailHit.FromHybrid).ToList(),
            _ => throw new McpException($"Unknown mode '{mode}'. Use 'keyword', 'semantic', or 'hybrid'."),
        };

        return new SearchEmailsResponse(query, resolvedMode, hits.Count, hits);
    }

    private int ClampLimit(int? requested)
    {
        var l = requested ?? _mcp.SearchDefaultLimit;
        if (l < 1) l = 1;
        if (l > _mcp.SearchMaxLimit) l = _mcp.SearchMaxLimit;
        return l;
    }

    private static string NormaliseMode(string mode) =>
        mode?.Trim().ToLowerInvariant() ?? "hybrid";

    private static SearchFilters BuildFilters(string? folder, string? dateFrom, string? dateTo, string? fromContains)
    {
        return new SearchFilters(
            Folder: string.IsNullOrWhiteSpace(folder) ? null : folder.Trim(),
            DateFrom: ParseDate(dateFrom, nameof(dateFrom)),
            DateTo: ParseDate(dateTo, nameof(dateTo)),
            FromContains: string.IsNullOrWhiteSpace(fromContains) ? null : fromContains.Trim());
    }

    private static DateTimeOffset? ParseDate(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto;
        throw new McpException($"{fieldName} '{value}' is not a valid ISO 8601 date.");
    }
}

public sealed record SearchEmailsResponse(
    string Query,
    string Mode,
    int Count,
    IReadOnlyList<EmailHit> Results);

/// <summary>
/// Unified hit shape across the three search modes. Mode-specific fields
/// (Bm25Score, Distance, Bm25Rank/VectorRank/RrfScore) are nullable so a
/// single record can carry results from any mode.
/// </summary>
public sealed record EmailHit(
    long Id,
    string MessageId,
    string Folder,
    string? Subject,
    string? FromAddress,
    string? FromName,
    DateTimeOffset? DateSent,
    string Snippet,
    double? Bm25Score = null,
    double? VectorDistance = null,
    double? RrfScore = null,
    int? Bm25Rank = null,
    int? VectorRank = null)
{
    public static EmailHit FromKeyword(Mailvec.Core.Models.SearchHit h) => new(
        Id: h.MessageId,
        MessageId: h.MessageIdHeader,
        Folder: h.Folder,
        Subject: h.Subject,
        FromAddress: h.FromAddress,
        FromName: h.FromName,
        DateSent: h.DateSent,
        Snippet: h.Snippet,
        Bm25Score: h.Bm25Score);

    public static EmailHit FromVector(VectorHit h) => new(
        Id: h.MessageId,
        MessageId: h.MessageIdHeader,
        Folder: h.Folder,
        Subject: h.Subject,
        FromAddress: h.FromAddress,
        FromName: h.FromName,
        DateSent: h.DateSent,
        Snippet: Truncate(h.ChunkText, 240),
        VectorDistance: h.Distance);

    public static EmailHit FromHybrid(HybridHit h) => new(
        Id: h.MessageId,
        MessageId: h.MessageIdHeader,
        Folder: h.Folder,
        Subject: h.Subject,
        FromAddress: h.FromAddress,
        FromName: h.FromName,
        DateSent: h.DateSent,
        Snippet: h.Snippet,
        RrfScore: h.RrfScore,
        Bm25Rank: h.Bm25Rank,
        VectorRank: h.VectorRank);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
