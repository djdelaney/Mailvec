using Mailvec.Core.Models;

namespace Mailvec.Core.Search;

public sealed record HybridHit(
    long MessageId,
    string MessageIdHeader,
    string Folder,
    string? Subject,
    string? FromAddress,
    string? FromName,
    DateTimeOffset? DateSent,
    string Snippet,
    double RrfScore,
    int? Bm25Rank,
    int? VectorRank,
    // Surfaced when the vector leg's top chunk for this message came from an
    // attachment rather than the body, so the MCP layer can tell Claude
    // "this email matched via its 2024-statement.pdf attachment".
    long? MatchedAttachmentId = null,
    int? MatchedAttachmentPartIndex = null,
    string? MatchedAttachmentFileName = null);

/// <summary>
/// Combines keyword (BM25) and vector results with Reciprocal Rank Fusion.
/// RRF score for a doc = sum over result lists of 1 / (k + rank). Doc rank
/// is 1-indexed; missing from a list contributes 0. k=60 follows the
/// standard RRF tuning in the literature.
/// </summary>
public sealed class HybridSearchService(KeywordSearchService keyword, VectorSearchService vector)
{
    private const int RrfK = 60;

    public async Task<IReadOnlyList<HybridHit>> SearchAsync(string query, int limit = 20, int candidatesPerLeg = 50, SearchFilters? filters = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        filters ??= SearchFilters.None;

        // vec0 KNN runs before the filter join; VectorSearchService owns the
        // k-escalation that keeps a restrictive filter from starving the vector
        // leg, so we just ask for `candidatesPerLeg` and let it fetch enough.
        var keywordHits = keyword.Search(query, limit: candidatesPerLeg, filters);
        var vectorHits = await vector.SearchAsync(query, limit: candidatesPerLeg, k: candidatesPerLeg * 2, filters, ct).ConfigureAwait(false);

        return Fuse(keywordHits, vectorHits, limit);
    }

    public static IReadOnlyList<HybridHit> Fuse(
        IReadOnlyList<SearchHit> keywordHits,
        IReadOnlyList<VectorHit> vectorHits,
        int limit)
    {
        var byMessage = new Dictionary<long, FusedRow>();

        for (int i = 0; i < keywordHits.Count; i++)
        {
            var h = keywordHits[i];
            ref var row = ref Get(byMessage, h.MessageId);
            row.Bm25Rank = i + 1;
            row.RrfScore += 1.0 / (RrfK + (i + 1));
            row.Subject ??= h.Subject;
            row.FromAddress ??= h.FromAddress;
            row.FromName ??= h.FromName;
            row.DateSent ??= h.DateSent;
            row.Folder ??= h.Folder;
            row.MessageIdHeader ??= h.MessageIdHeader;
            row.Snippet ??= h.Snippet;
        }

        for (int i = 0; i < vectorHits.Count; i++)
        {
            var h = vectorHits[i];
            ref var row = ref Get(byMessage, h.MessageId);
            row.VectorRank = i + 1;
            row.RrfScore += 1.0 / (RrfK + (i + 1));
            row.Subject ??= h.Subject;
            row.FromAddress ??= h.FromAddress;
            row.FromName ??= h.FromName;
            row.DateSent ??= h.DateSent;
            row.Folder ??= h.Folder;
            row.MessageIdHeader ??= h.MessageIdHeader;
            row.Snippet ??= Truncate(h.ChunkText, 240);
            // Vector leg knows the chunk source. Keep the *first* attachment
            // hit we see (which is the highest-ranked vector match for this
            // message). BM25 doesn't distinguish body vs attachment so we
            // always trust the vector leg for this signal.
            if (h.ChunkSource == "attachment" && row.MatchedAttachmentId is null)
            {
                row.MatchedAttachmentId = h.MatchedAttachmentId;
                row.MatchedAttachmentPartIndex = h.MatchedAttachmentPartIndex;
                row.MatchedAttachmentFileName = h.MatchedAttachmentFileName;
            }
        }

        return byMessage.Values
            .OrderByDescending(r => r.RrfScore)
            .Take(limit)
            .Select(r => new HybridHit(
                MessageId: r.MessageId,
                MessageIdHeader: r.MessageIdHeader ?? string.Empty,
                Folder: r.Folder ?? string.Empty,
                Subject: r.Subject,
                FromAddress: r.FromAddress,
                FromName: r.FromName,
                DateSent: r.DateSent,
                Snippet: r.Snippet ?? string.Empty,
                RrfScore: r.RrfScore,
                Bm25Rank: r.Bm25Rank,
                VectorRank: r.VectorRank,
                MatchedAttachmentId: r.MatchedAttachmentId,
                MatchedAttachmentPartIndex: r.MatchedAttachmentPartIndex,
                MatchedAttachmentFileName: r.MatchedAttachmentFileName))
            .ToList();
    }

    private static ref FusedRow Get(Dictionary<long, FusedRow> dict, long id)
    {
        ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dict, id, out _);
        if (slot.MessageId == 0) slot.MessageId = id;
        return ref slot;
    }

    private static string Truncate(string s, int maxLen) =>
        Parsing.StringTruncation.Truncate(s, maxLen);

    private struct FusedRow
    {
        public long MessageId;
        public string? MessageIdHeader;
        public string? Folder;
        public string? Subject;
        public string? FromAddress;
        public string? FromName;
        public DateTimeOffset? DateSent;
        public string? Snippet;
        public double RrfScore;
        public int? Bm25Rank;
        public int? VectorRank;
        public long? MatchedAttachmentId;
        public int? MatchedAttachmentPartIndex;
        public string? MatchedAttachmentFileName;
    }
}
