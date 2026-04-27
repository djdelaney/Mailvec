using Mailvec.Core.Embedding;
using Mailvec.Core.Options;

namespace Mailvec.Core.Tests.Embedding;

public class ChunkingServiceTests
{
    private static ChunkingService MakeService(int chunkTokens = 100, int overlapTokens = 10) =>
        new(Microsoft.Extensions.Options.Options.Create(new EmbedderOptions
        {
            ChunkSizeTokens = chunkTokens,
            ChunkOverlapTokens = overlapTokens,
        }));

    [Fact]
    public void Empty_or_whitespace_returns_no_chunks()
    {
        var svc = MakeService();
        svc.Chunk(null).ShouldBeEmpty();
        svc.Chunk("").ShouldBeEmpty();
        svc.Chunk("   \n\t\n").ShouldBeEmpty();
    }

    [Fact]
    public void Short_message_returns_single_chunk()
    {
        var svc = MakeService(chunkTokens: 100);   // ~400 chars
        var body = "Hi Bob,\n\nLunch at noon?\n\n— Alice";

        var chunks = svc.Chunk(body);
        chunks.Count.ShouldBe(1);
        chunks[0].Index.ShouldBe(0);
        chunks[0].Text.ShouldBe(body);
        chunks[0].EstimatedTokenCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Long_body_splits_across_paragraphs()
    {
        var svc = MakeService(chunkTokens: 50, overlapTokens: 0);   // ~200 chars per chunk
        // Three paragraphs ~150 chars each => should produce 2-3 chunks at this size.
        var p = new string('a', 150);
        var body = $"{p}\n\n{p}\n\n{p}";

        var chunks = svc.Chunk(body);
        chunks.Count.ShouldBeGreaterThan(1);
        chunks.Sum(c => c.Text.Length).ShouldBeGreaterThanOrEqualTo(body.Length - 100);
        // Indices monotonically increase from 0.
        chunks.Select(c => c.Index).ShouldBe(Enumerable.Range(0, chunks.Count));
    }

    [Fact]
    public void Overlap_carries_tail_of_previous_chunk_into_next()
    {
        var svc = MakeService(chunkTokens: 50, overlapTokens: 10);   // 200 char chunks, 40 char overlap
        var p = new string('a', 150);
        var marker = "DISTINCT_MARKER_PHRASE";
        var body = $"{p}{marker}\n\n{new string('b', 150)}";

        var chunks = svc.Chunk(body);
        chunks.Count.ShouldBeGreaterThan(1);
        // The marker sits near the end of the first chunk; with overlap, it should reappear at the start of the second.
        chunks[1].Text.ShouldContain(marker);
    }

    [Fact]
    public void Single_paragraph_longer_than_max_is_hard_split()
    {
        var svc = MakeService(chunkTokens: 25, overlapTokens: 5);   // 100 char chunks, 20 char overlap
        var body = new string('x', 350);                            // single 350-char block, no breaks

        var chunks = svc.Chunk(body);
        chunks.Count.ShouldBeGreaterThan(1);
        chunks.All(c => c.Text.Length <= 100).ShouldBeTrue();
        // Concatenated minus overlaps should reconstruct the original length, ±overlap.
        var totalCovered = chunks[0].Text.Length + chunks.Skip(1).Sum(c => c.Text.Length - 20);
        totalCovered.ShouldBeGreaterThanOrEqualTo(body.Length);
    }

    [Fact]
    public void Estimated_tokens_uses_four_chars_per_token_heuristic()
    {
        var svc = MakeService();
        var body = new string('a', 400);   // exactly 100 tokens at 4 chars/token

        var chunks = svc.Chunk(body);
        var single = chunks.Single();
        single.EstimatedTokenCount.ShouldBe(100);
    }
}
