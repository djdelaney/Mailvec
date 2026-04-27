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
    int? VectorRank);

/// <summary>
/// Combines keyword (BM25) and vector results with Reciprocal Rank Fusion.
/// RRF score for a doc = sum over result lists of 1 / (k + rank). Doc rank
/// is 1-indexed; missing from a list contributes 0. k=60 follows the
/// standard RRF tuning in the literature.
/// </summary>
public sealed class HybridSearchService(KeywordSearchService keyword, VectorSearchService vector)
{
    private const int RrfK = 60;

    public async Task<IReadOnlyList<HybridHit>> SearchAsync(string query, int limit = 20, int candidatesPerLeg = 50, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var keywordHits = keyword.Search(query, limit: candidatesPerLeg);
        var vectorHits = await vector.SearchAsync(query, limit: candidatesPerLeg, k: candidatesPerLeg * 2, ct).ConfigureAwait(false);

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
                VectorRank: r.VectorRank))
            .ToList();
    }

    private static ref FusedRow Get(Dictionary<long, FusedRow> dict, long id)
    {
        ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(dict, id, out _);
        if (slot.MessageId == 0) slot.MessageId = id;
        return ref slot;
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "…";

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
    }
}
