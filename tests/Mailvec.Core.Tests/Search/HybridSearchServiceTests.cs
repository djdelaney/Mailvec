using Mailvec.Core.Models;
using Mailvec.Core.Search;

namespace Mailvec.Core.Tests.Search;

public class HybridSearchServiceTests
{
    private static SearchHit Kw(long id, double score = -1.0) =>
        new(id, $"m{id}@x", "INBOX", $"subj {id}", "alice@x", "Alice", null, $"snip {id}", score);

    private static VectorHit Vec(long id, double distance = 0.1) =>
        new(id, $"m{id}@x", "INBOX", $"subj {id}", "alice@x", "Alice", null, ChunkId: id * 10, ChunkIndex: 0, ChunkText: $"chunk {id}", Distance: distance);

    [Fact]
    public void Fuses_disjoint_lists_into_a_combined_ranking()
    {
        var kw = new List<SearchHit> { Kw(1), Kw(2), Kw(3) };
        var vec = new List<VectorHit> { Vec(4), Vec(5), Vec(6) };

        var hits = HybridSearchService.Fuse(kw, vec, limit: 10);

        hits.Count.ShouldBe(6);
        // Top results should be the rank-1 entries from each list (tied) before rank-2/3.
        hits.Take(2).Select(h => h.MessageId).ShouldContain(1L);
        hits.Take(2).Select(h => h.MessageId).ShouldContain(4L);
    }

    [Fact]
    public void Documents_present_in_both_lists_outrank_singletons()
    {
        var kw = new List<SearchHit> { Kw(1), Kw(99) };       // doc 1 ranks #1 in keyword
        var vec = new List<VectorHit> { Vec(1), Vec(98) };    // doc 1 also #1 in vector

        var hits = HybridSearchService.Fuse(kw, vec, limit: 10);

        hits[0].MessageId.ShouldBe(1L);
        hits[0].Bm25Rank.ShouldBe(1);
        hits[0].VectorRank.ShouldBe(1);
        // Singletons follow.
        hits.Skip(1).Select(h => h.MessageId).ShouldContain(99L);
        hits.Skip(1).Select(h => h.MessageId).ShouldContain(98L);
    }

    [Fact]
    public void Lower_ranks_contribute_smaller_score_increments()
    {
        var kw = new List<SearchHit> { Kw(1), Kw(2), Kw(3), Kw(4), Kw(5) };
        var vec = new List<VectorHit>();

        var hits = HybridSearchService.Fuse(kw, vec, limit: 10);

        // Strict ordering by BM25 rank when only keyword leg contributes.
        hits.Select(h => h.MessageId).ShouldBe(new[] { 1L, 2L, 3L, 4L, 5L });
        hits[0].RrfScore.ShouldBeGreaterThan(hits[1].RrfScore);
        hits[1].RrfScore.ShouldBeGreaterThan(hits[4].RrfScore);
    }

    [Fact]
    public void Limit_caps_returned_results()
    {
        var kw = Enumerable.Range(1, 30).Select(i => Kw(i)).ToList();
        var vec = Enumerable.Range(1, 30).Select(i => Vec(i + 100)).ToList();

        var hits = HybridSearchService.Fuse(kw, vec, limit: 5);

        hits.Count.ShouldBe(5);
    }
}
