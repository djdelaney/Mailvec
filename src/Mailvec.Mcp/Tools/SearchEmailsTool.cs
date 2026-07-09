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
    IOptions<OllamaOptions> ollamaOptions,
    IOptions<ArchiveOptions> archiveOptions,
    ToolCallLogger callLog)
{
    private readonly McpOptions _mcp = mcpOptions.Value;
    private readonly FastmailOptions _fastmail = fastmailOptions.Value;
    private readonly OllamaOptions _ollama = ollamaOptions.Value;
    private readonly ArchiveOptions _archive = archiveOptions.Value;
    private const string ToolName = "search_emails";

    [McpServerTool(Name = "search_emails")]
    [Description(
        "Search or browse the local email archive. " +
        "If `query` is provided, runs hybrid (keyword + semantic) ranked search by default. " +
        "If `query` is omitted, returns the most recent messages matching the filters, sorted by date descending — " +
        "use this for 'show me my recent INBOX mail' or 'find all email from invoice@anthropic.com' style requests. " +
        "For attachment-seeking asks ('what PDFs did I get last month', 'that email with the spreadsheet from Dan'), " +
        "set `attachmentType` (e.g. 'pdf', 'image') or `hasAttachments=true` — with or without a query. " +
        "The archive may span 10+ years and hundreds of thousands of messages; every response includes " +
        "`archiveStats` (totalMessages, oldestDate, latestDate) so you can gauge actual scope, and " +
        "`appliedFilters` echoing the filters you used. " +
        "Strongly prefer setting `dateFrom`/`dateTo` whenever the user's question implies a time window " +
        "('last week', 'last quarter', 'in 2023', 'recently', 'before I left $job', 'this year'); on a " +
        "10-year archive, an unbounded query skews toward old mail and dilutes recent context. When in " +
        "doubt for casual 'recently'-style asks, a 12-month lower bound is a safe default. " +
        "Each result carries the internal id, RFC message_id, folder, sender, date, snippet, and (for ranked queries) score breakdown. " +
        "When a match was driven by content inside a PDF/DOCX/text attachment rather than the email body, the result MAY include " +
        "`matchedAttachment` with the attachment's partIndex and filename (semantic matches report it; keyword-only matches " +
        "can't attribute a specific attachment, so its absence doesn't mean the body matched) — use those with " +
        "`get_attachment_text` to read the document (or `get_attachment_page_image` for a PDF page, `view_attachment` for an image). " +
        "Use a result's id or messageId with get_email/get_thread for follow-up. " +
        "Each result also includes `webmailUrl` (the raw deep-link) and `webmailLink` (a ready-made, correctly-escaped " +
        "Markdown link), both populated when the user has configured their webmail account id. " +
        "When you cite or quote a specific result to the user, render its `webmailLink` **verbatim** so they can one-click " +
        "through — do NOT build your own link from `subject` and `webmailUrl`, because the subject is untrusted email " +
        "content and a crafted subject can spoof the link target. Skip the link only when `webmailLink` is null or when the " +
        "user has explicitly asked for terse output.")]
    public async Task<SearchEmailsResponse> SearchEmails(
        [Description("Optional free-text query. With it, results are ranked by relevance; without it, by date descending. " +
                     "For mode=keyword this is an FTS5 expression (phrase quotes, AND/OR/NOT). For mode=semantic/hybrid it's natural language. " +
                     "When searching for mail tied to a specific company (purchases, receipts, notifications, account alerts), " +
                     "prefer the company's domain (e.g. `target.com`) over the brand name (`target`). The domain appears in sender " +
                     "addresses, body links, and footer/unsubscribe text, and is far more discriminating than the brand name " +
                     "(which collides with the common English word and pulls in unrelated mail). For 'all email FROM this company' " +
                     "questions, scoping with `fromContains` (or `fromExact`) is even sharper than putting the domain in `query`.")]
        string? query = null,
        [Description("Search mode. Strongly prefer 'hybrid' (default): it fuses BM25 keyword ranking with vector similarity " +
                     "via reciprocal rank fusion, and on this archive matches or beats either leg alone for almost every query. " +
                     "Only drop to 'semantic' (vector only) when the user's query is purely conceptual AND contains no proper " +
                     "nouns, company/people names, domains, invoice/order numbers, URLs, or other exact-match tokens — pure " +
                     "vector loses the BM25 signal that catches those. Only use 'keyword' (BM25 only) when you have a precise " +
                     "FTS5 expression (phrase quotes, AND/OR/NOT) and want to bypass semantic ranking entirely. Ignored when " +
                     "query is omitted.")]
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
        [Description("Case-insensitive substring against from_address OR from_name. Useful when only the sender's name or domain is known. " +
                     "For mail from a specific company, prefer the domain (`target.com`) over the brand name (`target`) — the domain " +
                     "is in the from_address and is far more discriminating. Ignored if fromExact is set.")]
        string? fromContains = null,
        [Description("Case-insensitive exact match on from_address (the email address only, not the display name). Use for 'all email from <addr>' lookups.")]
        string? fromExact = null,
        [Description("true = only messages with at least one attachment (including inline images); false = only messages without. " +
                     "Combine with omitted `query` to browse 'recent mail with attachments from X'.")]
        bool? hasAttachments = null,
        [Description("Only messages with at least one attachment of this type: the token 'image' (any image/*), or a filename " +
                     "extension like 'pdf', 'docx', 'xlsx', 'csv' (leading dot optional). Matches the attachment's filename " +
                     "suffix or its known MIME type, so mislabeled attachments still match. Implies hasAttachments=true.")]
        string? attachmentType = null,
        CancellationToken ct = default)
    {
        var startTs = callLog.LogCall(ToolName, new { query, mode, limit, folder, dateFrom, dateTo, fromContains, fromExact, hasAttachments, attachmentType });

        var resolvedLimit = ClampLimit(limit);
        var filters = BuildFilters(folder, dateFrom, dateTo, fromContains, fromExact, hasAttachments, attachmentType);
        var archiveStats = messages.GetArchiveStats();
        var appliedFilters = AppliedFilters.From(filters);
        // Non-null only when the archive has zero messages: tells the client
        // LLM WHY it's empty (installer never ran vs indexer hasn't caught up)
        // instead of letting "0 results" read as "your mail contains nothing".
        var setupHint = SetupHints.EmptyArchiveHint(
            archiveStats.TotalMessages,
            Mailvec.Core.Options.SharedConfig.SharedConfigFileExists(),
            Mailvec.Core.PathExpansion.Expand(_archive.DatabasePath));

        // Query-less path: just list filter-matching messages by date.
        if (string.IsNullOrWhiteSpace(query))
        {
            var rows = messages.BrowseByFilters(filters, resolvedLimit);
            var browseHits = rows.Select(EmailHit.FromMessage).Select(WithWebmailUrl).ToList();
            var browseResp = new SearchEmailsResponse(Query: null, Mode: "browse", browseHits.Count, browseHits, archiveStats, appliedFilters, setupHint);
            callLog.LogResult(ToolName, BuildResultSummary(browseResp), startTs);
            return browseResp;
        }

        var resolvedMode = NormaliseMode(mode);

        IReadOnlyList<EmailHit> hits;
        try
        {
            hits = resolvedMode switch
            {
                "keyword" => keyword.Search(query, resolvedLimit, filters).Select(EmailHit.FromKeyword).Select(WithWebmailUrl).ToList(),
                "semantic" => (await vector.SearchAsync(query, resolvedLimit, k: Math.Max(100, resolvedLimit * 5), filters, ct).ConfigureAwait(false))
                    .Select(EmailHit.FromVector).Select(WithWebmailUrl).ToList(),
                "hybrid" => (await hybrid.SearchAsync(query, resolvedLimit, filters: filters, ct: ct).ConfigureAwait(false))
                    .Select(EmailHit.FromHybrid).Select(WithWebmailUrl).ToList(),
                _ => throw new McpException($"Unknown mode '{mode}'. Use 'keyword', 'semantic', or 'hybrid'."),
            };
        }
        catch (HttpRequestException ex)
        {
            // The MCP SDK collapses any non-McpException into a bare
            // "An error occurred." — invisible to the client LLM. The only
            // HTTP dependency in the search path is the query-embedding call,
            // so translate it into something the client can act on (this is
            // the single most common broken state on a fresh install: Ollama
            // not running, or the embedding model never pulled).
            throw new McpException(
                $"Semantic ranking is unavailable — the embedding call to Ollama at {_ollama.BaseUrl} failed ({ex.Message}). " +
                $"Keyword search still works: retry this query with mode=keyword. " +
                $"To restore semantic/hybrid search, make sure Ollama is running and the embedding model is pulled " +
                $"(`ollama pull {_ollama.EmbeddingModel}`); `mailvec doctor` gives a precise diagnosis.");
        }

        var response = new SearchEmailsResponse(query, resolvedMode, hits.Count, hits, archiveStats, appliedFilters, setupHint);
        callLog.LogResult(ToolName, BuildResultSummary(response), startTs);
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
    private EmailHit WithWebmailUrl(EmailHit h)
    {
        var url = WebmailLinkBuilder.Build(h.MessageId, _fastmail);
        return h with { WebmailUrl = url, WebmailLink = WebmailLinkBuilder.MarkdownLink(url, h.Subject) };
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

    private static SearchFilters BuildFilters(string? folder, string? dateFrom, string? dateTo, string? fromContains, string? fromExact, bool? hasAttachments, string? attachmentType)
    {
        return new SearchFilters(
            Folder: string.IsNullOrWhiteSpace(folder) ? null : folder.Trim(),
            DateFrom: ParseDate(dateFrom, nameof(dateFrom), isUpperBound: false),
            DateTo: ParseDate(dateTo, nameof(dateTo), isUpperBound: true),
            FromContains: string.IsNullOrWhiteSpace(fromContains) ? null : fromContains.Trim(),
            FromExact: string.IsNullOrWhiteSpace(fromExact) ? null : fromExact.Trim(),
            HasAttachments: hasAttachments,
            AttachmentType: string.IsNullOrWhiteSpace(attachmentType) ? null : attachmentType.Trim());
    }

    // Delegates to the shared Core parser so the "date-only dateTo means end
    // of that day" rule stays identical across MCP / tray / CLI.
    private static DateTimeOffset? ParseDate(string? value, string fieldName, bool isUpperBound)
    {
        if (Mailvec.Core.Search.SearchDateParser.TryParse(value, isUpperBound, out var bound))
            return bound;
        throw new McpException($"{fieldName} '{value}' is not a valid ISO 8601 date.");
    }
}

public sealed record SearchEmailsResponse(
    string? Query,
    string Mode,
    int Count,
    IReadOnlyList<EmailHit> Results,
    Mailvec.Core.Models.ArchiveStats ArchiveStats,
    AppliedFilters AppliedFilters,
    // Additive (nullable, omitted-when-null for older clients' purposes is
    // fine — it serializes as null): populated only when the archive has zero
    // messages, explaining why and what to do. See SetupHints.
    string? SetupHint = null);

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
    string? FromExact,
    bool? HasAttachments = null,
    string? AttachmentType = null)
{
    public static AppliedFilters From(Mailvec.Core.Search.SearchFilters f) => new(
        Folder: f.Folder,
        DateFrom: f.DateFrom?.ToString("O"),
        DateTo: f.DateTo?.ToString("O"),
        FromContains: f.FromContains,
        FromExact: f.FromExact,
        HasAttachments: f.HasAttachments,
        AttachmentType: f.AttachmentType);
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
    // Surfaced when the top-ranked chunk for this message came from an
    // attachment rather than the body — answers Claude's "why did this email
    // match my query?" without a follow-up call. Pair (PartIndex, FileName)
    // is also exactly what the attachment tools need to fetch the source.
    MatchedAttachment? MatchedAttachment = null,
    // Decorated post-construction by SearchEmailsTool.WithWebmailUrl when
    // Fastmail:AccountId is configured. The factory methods below leave them null.
    string? WebmailUrl = null,
    // A ready-to-render, subject-escaped Markdown link — clients should render
    // this verbatim rather than assembling one from the untrusted Subject.
    string? WebmailLink = null)
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
        VectorDistance: h.Distance,
        MatchedAttachment: h.ChunkSource == "attachment" && h.MatchedAttachmentPartIndex is { } pi
            ? new MatchedAttachment(pi, h.MatchedAttachmentFileName)
            : null);

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
        VectorRank: h.VectorRank,
        MatchedAttachment: h.MatchedAttachmentPartIndex is { } pi
            ? new MatchedAttachment(pi, h.MatchedAttachmentFileName)
            : null);

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

    private static string Truncate(string s, int max) => Mailvec.Core.Parsing.StringTruncation.Truncate(s, max);
}

/// <summary>
/// Tells the caller a search hit matched via an email attachment, not the
/// body. <see cref="PartIndex"/> + parent message id are the inputs to
/// <c>get_attachment_text</c> / <c>view_attachment</c>, so Claude can fetch
/// the document directly.
/// </summary>
public sealed record MatchedAttachment(int PartIndex, string? FileName);
