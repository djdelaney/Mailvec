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
    public void Does_not_emit_a_duplicate_overlap_only_chunk_before_a_hard_split()
    {
        // A normal paragraph followed by an unbroken block longer than the chunk
        // size: the paragraph flushes (seeding an overlap tail), then the block
        // is hard-split. The carried-over overlap must NOT be emitted as its own
        // chunk (a verbatim duplicate of the previous chunk's ending).
        var svc = MakeService(chunkTokens: 50, overlapTokens: 10);  // 200 chars, 40-char overlap
        var para = new string('a', 150);
        var block = new string('b', 500);   // > 200 → hard-split
        var body = $"{para}\n\n{block}";

        var chunks = svc.Chunk(body);

        // Exactly one all-'a' chunk (the paragraph). Before the fix a second
        // ~40-char all-'a' chunk (the orphaned overlap tail) was emitted.
        chunks.Count(c => c.Text.Length > 0 && c.Text.All(ch => ch == 'a')).ShouldBe(1);
        // Indices stay contiguous after the fix.
        chunks.Select(c => c.Index).ShouldBe(Enumerable.Range(0, chunks.Count));
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

    [Theory]
    [InlineData(200, 200)]   // overlap == size: HardSplit's step clamps to 1 char
    [InlineData(200, 300)]   // overlap > size
    [InlineData(200, 199)]   // the near-miss a strict `overlap < size` rule would admit
    [InlineData(200, 101)]   // just past half
    [InlineData(32, 32)]     // ChunkSizeTokens lowered for an experiment, overlap left at its default
    public void Refuses_an_overlap_above_half_the_chunk_size(int chunkTokens, int overlapTokens)
    {
        // HardSplit slides by (size - overlap), so the chunk count for unbroken
        // text grows as size/(size - overlap): 4,201 chunks from 5,000 chars at
        // 200/200, and still ~200x at 199/200 — which is why the rule is "at
        // most half" and not merely "less than". Each chunk is a real embedding
        // request, so this lands on Ollama and the chunks table too.
        //
        // The 32/32 case is the realistic one and the reason this refuses at
        // construction rather than clamping quietly: the documented experiment
        // knob is ChunkSizeTokens (see docs/contributing/embedding-experiments.md),
        // ChunkOverlapTokens defaults to 32, and nothing about lowering the
        // former hints that you must lower the latter.
        var ex = Should.Throw<InvalidOperationException>(() => MakeService(chunkTokens, overlapTokens));

        // Both values named, because the fix is to change one of them and the
        // operator can't tell which without seeing the pair.
        ex.Message.ShouldContain(overlapTokens.ToString());
        ex.Message.ShouldContain(chunkTokens.ToString());
        ex.Message.ShouldContain("ChunkOverlapTokens");
        ex.Message.ShouldContain("ChunkSizeTokens");
    }

    [Theory]
    [InlineData(200, 100)]   // exactly half — allowed, and the worst permitted case
    [InlineData(200, 32)]    // the shipped default ratio
    [InlineData(200, 0)]     // overlap off entirely
    [InlineData(1, 0)]       // degenerate but coherent: no room for any overlap
    public void Accepts_an_overlap_of_at_most_half_the_chunk_size(int chunkTokens, int overlapTokens)
    {
        var svc = MakeService(chunkTokens, overlapTokens);

        // At the boundary the step is half the window, so an unbroken block
        // yields ~2 chunks per window rather than one per character.
        var chunks = svc.Chunk(new string('x', chunkTokens * 4 * 5));
        chunks.Count.ShouldBeLessThanOrEqualTo(11);
        chunks.ShouldAllBe(c => c.Text.Length <= chunkTokens * 4);
    }
}
