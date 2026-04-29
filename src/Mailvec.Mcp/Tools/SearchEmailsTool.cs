using System.ComponentModel;
using System.Globalization;
using Mailvec.Core.Data;
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
    MessageRepository messages,
    IOptions<McpOptions> mcpOptions,
    IOptions<FastmailOptions> fastmailOptions,
    ToolCallLogger callLog)
{
    private readonly McpOptions _mcp = mcpOptions.Value;
    private readonly FastmailOptions _fastmail = fastmailOptions.Value;
    private const string ToolName = "search_emails";

    [McpServerTool(Name = "search_emails")]
    [Description(
        "Search or browse the local email archive. " +
        "If `query` is provided, runs hybrid (keyword + semantic) ranked search by default. " +
        "If `query` is omitted, returns the most recent messages matching the filters, sorted by date descending — " +
        "use this for 'show me my recent INBOX mail' or 'find all email from invoice@anthropic.com' style requests. " +
        "The archive may span 10+ years and hundreds of thousands of messages; every response includes " +
        "`archiveStats` (totalMessages, oldestDate, latestDate) so you can gauge actual scope, and " +
        "`appliedFilters` echoing the filters you used. " +
        "Strongly prefer setting `dateFrom`/`dateTo` whenever the user's question implies a time window " +
        "('last week', 'last quarter', 'in 2023', 'recently', 'before I left $job', 'this year'); on a " +
        "10-year archive, an unbounded query skews toward old mail and dilutes recent context. When in " +
        "doubt for casual 'recently'-style asks, a 12-month lower bound is a safe default. " +
        "Each result carries the internal id, RFC message_id, folder, sender, date, snippet, and (for ranked queries) score breakdown. " +
        "Use a result's id or messageId with get_email/get_thread for follow-up.")]
    public async Task<SearchEmailsResponse> SearchEmails(
        [Description("Optional free-text query. With it, results are ranked by relevance; without it, by date descending. " +
                     "For mode=keyword this is an FTS5 expression (phrase quotes, AND/OR/NOT). For mode=semantic/hybrid it's natural language.")]
        string? query = null,
        [Description("Search mode: 'hybrid' (default), 'keyword' (BM25 only), or 'semantic' (vector only). Ignored when query is omitted.")]
        string mode = "hybrid",
        [Description("Max number of results to return. Server caps this at the configured SearchMaxLimit.")]
        int? limit = null,
        [Description("Restrict to a single folder, exact match (e.g. 'INBOX', 'Archive.2024').")]
        string? folder = null,
        [Description("Earliest message date (inclusive), ISO 8601, e.g. '2024-01-01' or '2024-01-01T00:00:00Z'. " +
                     "Set this for any time-bounded question — even a loose lower bound (e.g. one year ago) " +
                     "materially improves relevance on a multi-year archive. Omit only when the user is " +
                     "explicitly asking across all-time history (e.g. 'the oldest email from X').")]
        string? dateFrom = null,
        [Description("Latest message date (inclusive), ISO 8601. Pair with `dateFrom` for explicit windows; " +
                     "omit to mean 'up to the present'.")]
        string? dateTo = null,
        [Description("Case-insensitive substring against from_address OR from_name. Useful when only the sender's name or domain is known. Ignored if fromExact is set.")]
        string? fromContains = null,
        [Description("Case-insensitive exact match on from_address (the email address only, not the display name). Use for 'all email from <addr>' lookups.")]
        string? fromExact = null,
        CancellationToken ct = default)
    {
        callLog.LogCall(ToolName, new { query, mode, limit, folder, dateFrom, dateTo, fromContains, fromExact });

        var resolvedLimit = ClampLimit(limit);
        var filters = BuildFilters(folder, dateFrom, dateTo, fromContains, fromExact);
        var archiveStats = messages.GetArchiveStats();
        var appliedFilters = AppliedFilters.From(filters);

        // Query-less path: just list filter-matching messages by date.
        if (string.IsNullOrWhiteSpace(query))
        {
            var rows = messages.BrowseByFilters(filters, resolvedLimit);
            var browseHits = rows.Select(EmailHit.FromMessage).Select(WithWebmailUrl).ToList();
            var browseResp = new SearchEmailsResponse(Query: null, Mode: "browse", browseHits.Count, browseHits, archiveStats, appliedFilters);
            callLog.LogResult(ToolName, BuildResultSummary(browseResp));
            return browseResp;
        }

        var resolvedMode = NormaliseMode(mode);

        IReadOnlyList<EmailHit> hits = resolvedMode switch
        {
            "keyword" => keyword.Search(query, resolvedLimit, filters).Select(EmailHit.FromKeyword).Select(WithWebmailUrl).ToList(),
            "semantic" => (await vector.SearchAsync(query, resolvedLimit, k: Math.Max(100, resolvedLimit * 5), filters, ct).ConfigureAwait(false))
                .Select(EmailHit.FromVector).Select(WithWebmailUrl).ToList(),
            "hybrid" => (await hybrid.SearchAsync(query, resolvedLimit, filters: filters, ct: ct).ConfigureAwait(false))
                .Select(EmailHit.FromHybrid).Select(WithWebmailUrl).ToList(),
            _ => throw new McpException($"Unknown mode '{mode}'. Use 'keyword', 'semantic', or 'hybrid'."),
        };

        var response = new SearchEmailsResponse(query, resolvedMode, hits.Count, hits, archiveStats, appliedFilters);
        callLog.LogResult(ToolName, BuildResultSummary(response));
        return response;
    }

    private static object BuildResultSummary(SearchEmailsResponse r) => new
    {
        mode = r.Mode,
        count = r.Count,
        // Top hits give enough context to correlate the call against the archive
        // without dumping full bodies into the log.
        top = r.Results.Take(5).Select(h => new
        {
            id = h.Id,
            from = h.FromAddress,
            date = h.DateSent,
            subject = h.Subject,
        }),
    };

    /// <summary>Decorates a hit with a Fastmail webmail link if AccountId is configured.</summary>
    private EmailHit WithWebmailUrl(EmailHit h) =>
        h with { WebmailUrl = WebmailLinkBuilder.Build(h.MessageId, _fastmail) };

    private int ClampLimit(int? requested)
    {
        var l = requested ?? _mcp.SearchDefaultLimit;
        if (l < 1) l = 1;
        if (l > _mcp.SearchMaxLimit) l = _mcp.SearchMaxLimit;
        return l;
    }

    private static string NormaliseMode(string mode) =>
        mode?.Trim().ToLowerInvariant() ?? "hybrid";

    private static SearchFilters BuildFilters(string? folder, string? dateFrom, string? dateTo, string? fromContains, string? fromExact)
    {
        return new SearchFilters(
            Folder: string.IsNullOrWhiteSpace(folder) ? null : folder.Trim(),
            DateFrom: ParseDate(dateFrom, nameof(dateFrom)),
            DateTo: ParseDate(dateTo, nameof(dateTo)),
            FromContains: string.IsNullOrWhiteSpace(fromContains) ? null : fromContains.Trim(),
            FromExact: string.IsNullOrWhiteSpace(fromExact) ? null : fromExact.Trim());
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
    string? Query,
    string Mode,
    int Count,
    IReadOnlyList<EmailHit> Results,
    Mailvec.Core.Models.ArchiveStats ArchiveStats,
    AppliedFilters AppliedFilters);

/// <summary>
/// Echo of the filters the server actually applied for this call. Surfaced on
/// every response so Claude can self-correct when it forgets to scope a query
/// (e.g. "I left dateFrom null and the archive spans 10 years — let me retry
/// with a window"). Mirrors <see cref="Mailvec.Core.Search.SearchFilters"/>
/// shape but uses the wire-format date strings, since that's what Claude
/// passed in and what's most useful in the response.
/// </summary>
public sealed record AppliedFilters(
    string? Folder,
    string? DateFrom,
    string? DateTo,
    string? FromContains,
    string? FromExact)
{
    public static AppliedFilters From(Mailvec.Core.Search.SearchFilters f) => new(
        Folder: f.Folder,
        DateFrom: f.DateFrom?.ToString("O"),
        DateTo: f.DateTo?.ToString("O"),
        FromContains: f.FromContains,
        FromExact: f.FromExact);
}

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
    int? VectorRank = null,
    // Decorated post-construction by SearchEmailsTool.WithWebmailUrl when
    // Fastmail:AccountId is configured. The factory methods below leave it null.
    string? WebmailUrl = null)
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

    /// <summary>
    /// Used for the query-less browse path, where there's no score to report
    /// and no FTS snippet — produce a short body excerpt so the row is still
    /// useful to Claude without forcing a follow-up get_email call.
    /// </summary>
    public static EmailHit FromMessage(Mailvec.Core.Models.Message m) => new(
        Id: m.Id,
        MessageId: m.MessageId,
        Folder: m.Folder,
        Subject: m.Subject,
        FromAddress: m.FromAddress,
        FromName: m.FromName,
        DateSent: m.DateSent,
        Snippet: BuildBrowseSnippet(m.BodyText));

    private static string BuildBrowseSnippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        // Collapse whitespace so HTML-derived bodies don't waste the snippet on linebreaks.
        var collapsed = System.Text.RegularExpressions.Regex.Replace(body.Trim(), @"\s+", " ");
        return Truncate(collapsed, 240);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
