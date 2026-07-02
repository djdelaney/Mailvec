using System.Globalization;
using Mailvec.Core.Data;
using Mailvec.Core.Models;
using Mailvec.Core.Options;
using Mailvec.Core.Search;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Tray;

/// <summary>
/// Tray-facing wrapper around the same search services the MCP
/// <c>search_emails</c> tool uses. Returns a flat <see cref="TraySearchHit"/>
/// shape with a single unified <c>score</c> field (the SwiftUI result row
/// renders a vertical score bar from it) plus the BM25 + vector breakdowns
/// for the expanded preview's debug strip.
/// </summary>
public sealed class TraySearchService(
    KeywordSearchService keyword,
    VectorSearchService vector,
    HybridSearchService hybrid,
    MessageRepository messages,
    IOptions<McpOptions> mcpOptions,
    IOptions<FastmailOptions> fastmailOptions)
{
    public async Task<TraySearchResponse> SearchAsync(TraySearchRequest req, CancellationToken ct = default)
    {
        var mode = string.IsNullOrWhiteSpace(req.Mode) ? "hybrid" : req.Mode.Trim().ToLowerInvariant();
        var limit = ClampLimit(req.Limit);
        var filters = BuildFilters(req);

        // Query-less browse path: just list by date.
        if (string.IsNullOrWhiteSpace(req.Query))
        {
            var rows = messages.BrowseByFilters(filters, limit);
            var hits = rows.Select((m, idx) => new TraySearchHit(
                Id: m.Id,
                MessageId: m.MessageId,
                Folder: m.Folder,
                Subject: m.Subject,
                FromAddress: m.FromAddress,
                FromName: m.FromName,
                DateSent: m.DateSent,
                Snippet: BuildBrowseSnippet(m.BodyText),
                Score: 1.0 - (idx / (double)Math.Max(1, rows.Count)),
                Bm25Score: null,
                VectorScore: null,
                MatchedAttachment: null,
                WebmailUrl: WebmailLinkBuilder.Build(m.MessageId, fastmailOptions.Value)))
                .ToList();
            return new TraySearchResponse(req.Query, "browse", hits.Count, hits);
        }

        IReadOnlyList<TraySearchHit> results = mode switch
        {
            "keyword" => MapKeyword(keyword.Search(req.Query!, limit, filters)),
            "semantic" => MapVector(await vector.SearchAsync(req.Query!, limit, k: Math.Max(100, limit * 5), filters, ct).ConfigureAwait(false)),
            "hybrid" => MapHybrid(await hybrid.SearchAsync(req.Query!, limit, filters: filters, ct: ct).ConfigureAwait(false)),
            _ => throw new ArgumentException($"Unknown mode '{req.Mode}'. Use keyword/semantic/hybrid."),
        };

        return new TraySearchResponse(req.Query, mode, results.Count, results);
    }

    private IReadOnlyList<TraySearchHit> MapKeyword(IReadOnlyList<SearchHit> hits)
    {
        // FTS5 bm25() is NEGATIVE (more negative = better match). Normalize
        // to (0,1] by dividing by the BEST (most negative) score, so the top
        // hit renders a full score bar and weaker hits scale down. Dividing
        // by Max() — the score closest to zero, i.e. the WORST hit — produced
        // ratios >= 1 for every row, so keyword mode drew all-full bars.
        var best = hits.Count == 0 ? -1.0 : hits.Min(h => h.Bm25Score);
        return [..
            hits.Select(h => new TraySearchHit(
                Id: h.MessageId,
                MessageId: h.MessageIdHeader,
                Folder: h.Folder,
                Subject: h.Subject,
                FromAddress: h.FromAddress,
                FromName: h.FromName,
                DateSent: h.DateSent,
                Snippet: h.Snippet,
                Score: best == 0 ? 0 : Math.Clamp(h.Bm25Score / best, 0, 1),
                Bm25Score: h.Bm25Score,
                VectorScore: null,
                MatchedAttachment: null,
                WebmailUrl: WebmailLinkBuilder.Build(h.MessageIdHeader, fastmailOptions.Value)))];
    }

    private IReadOnlyList<TraySearchHit> MapVector(IReadOnlyList<VectorHit> hits)
    {
        return [..
            hits.Select(h => new TraySearchHit(
                Id: h.MessageId,
                MessageId: h.MessageIdHeader,
                Folder: h.Folder,
                Subject: h.Subject,
                FromAddress: h.FromAddress,
                FromName: h.FromName,
                DateSent: h.DateSent,
                Snippet: Truncate(h.ChunkText, 240),
                Score: 1.0 / (1.0 + Math.Max(0, h.Distance)),
                Bm25Score: null,
                VectorScore: 1.0 - Math.Clamp(h.Distance, 0, 1),
                MatchedAttachment: h.ChunkSource == "attachment" && h.MatchedAttachmentPartIndex is { } pi
                    ? new TrayMatchedAttachment(pi, h.MatchedAttachmentFileName, SizeHint: null)
                    : null,
                WebmailUrl: WebmailLinkBuilder.Build(h.MessageIdHeader, fastmailOptions.Value)))];
    }

    private IReadOnlyList<TraySearchHit> MapHybrid(IReadOnlyList<HybridHit> hits)
    {
        var max = hits.Count == 0 ? 1 : hits.Max(h => h.RrfScore);
        return [..
            hits.Select(h => new TraySearchHit(
                Id: h.MessageId,
                MessageId: h.MessageIdHeader,
                Folder: h.Folder,
                Subject: h.Subject,
                FromAddress: h.FromAddress,
                FromName: h.FromName,
                DateSent: h.DateSent,
                Snippet: h.Snippet,
                Score: max == 0 ? 0 : h.RrfScore / max,
                Bm25Score: null,
                VectorScore: null,
                MatchedAttachment: h.MatchedAttachmentPartIndex is { } pi
                    ? new TrayMatchedAttachment(pi, h.MatchedAttachmentFileName, SizeHint: null)
                    : null,
                WebmailUrl: WebmailLinkBuilder.Build(h.MessageIdHeader, fastmailOptions.Value)))];
    }

    private int ClampLimit(int? requested)
    {
        var l = requested ?? mcpOptions.Value.SearchDefaultLimit;
        if (l < 1) l = 1;
        if (l > mcpOptions.Value.SearchMaxLimit) l = mcpOptions.Value.SearchMaxLimit;
        return l;
    }

    private static SearchFilters BuildFilters(TraySearchRequest req) => new(
        Folder: string.IsNullOrWhiteSpace(req.Folder) ? null : req.Folder.Trim(),
        DateFrom: ParseDate(req.DateFrom, isUpperBound: false),
        DateTo: ParseDate(req.DateTo, isUpperBound: true),
        FromContains: string.IsNullOrWhiteSpace(req.FromContains) ? null : req.FromContains.Trim(),
        FromExact: string.IsNullOrWhiteSpace(req.FromExact) ? null : req.FromExact.Trim());

    private static DateTimeOffset? ParseDate(string? value, bool isUpperBound) =>
        Search.SearchDateParser.TryParse(value, isUpperBound, out var bound) ? bound : null;

    private static string BuildBrowseSnippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        var collapsed = System.Text.RegularExpressions.Regex.Replace(body.Trim(), @"\s+", " ");
        return Truncate(collapsed, 240);
    }

    private static string Truncate(string s, int max) => Parsing.StringTruncation.Truncate(s, max);
}
