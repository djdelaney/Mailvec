using Mailvec.Core.Options;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Embedding;

public sealed record TextChunk(
    int Index,
    string Text,
    int EstimatedTokenCount,
    // 'body' or 'attachment'. Defaults to 'body' so existing callers
    // (and tests) don't need updating; the embedder sets 'attachment'
    // for chunks derived from attachments.extracted_text and pairs them
    // with the source attachment's id.
    string Source = "body",
    long? AttachmentId = null);

/// <summary>
/// Splits a message body into overlapping chunks sized for the embedding
/// model's context window. Token counts are estimated at ~4 chars/token.
/// Prefers paragraph/sentence breaks; falls back to character-level cuts for
/// long unbroken text. Short messages return a single chunk.
/// </summary>
public sealed class ChunkingService
{
    private const int CharsPerToken = 4;

    private readonly int _maxChars;
    private readonly int _overlapChars;

    /// <summary>
    /// Refuses an overlap above half the chunk size, because past that point the
    /// chunker stops making forward progress in any bounded way.
    /// </summary>
    /// <remarks>
    /// <see cref="HardSplit"/> slides its window by <c>size - overlap</c>, so the
    /// chunk count for a block of unbroken text grows as
    /// <c>size / (size - overlap)</c> — unbounded as overlap approaches size, and
    /// at overlap &gt;= size the step clamps to 1 and it emits one near-duplicate
    /// max-size chunk PER CHARACTER (5,000 chars of unbroken text produced 4,201
    /// chunks at 200/200). Each one is a real embedding request, so the cost
    /// lands on Ollama and the chunks table, not just memory.
    ///
    /// Half is the line rather than a strict <c>overlap &lt; size</c>, because
    /// strict inequality bounds nothing: 199/200 still inflates the chunk count
    /// 200x. Half caps the inflation at 2x.
    ///
    /// Throwing rather than clamping, and here rather than deep in the loop:
    /// the realistic way in is NOT someone raising the overlap, it's someone
    /// LOWERING ChunkSizeTokens — the knob docs/contributing/embedding-experiments.md
    /// tells you to sweep — while ChunkOverlapTokens sits at its default 32.
    /// Anything at or below 64 crosses this line silently. That operator is
    /// about to spend hours on a re-embed and then read the eval numbers as a
    /// property of the chunk size, so a refusal at startup naming both values is
    /// worth far more than a clamp they never see.
    /// </remarks>
    public ChunkingService(IOptions<EmbedderOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sizeTokens = Math.Max(1, options.Value.ChunkSizeTokens);
        var overlapTokens = Math.Max(0, options.Value.ChunkOverlapTokens);
        var maxOverlapTokens = sizeTokens / 2;

        if (overlapTokens > maxOverlapTokens)
        {
            throw new InvalidOperationException(
                $"Embedder:ChunkOverlapTokens ({overlapTokens}) must be at most half of " +
                $"Embedder:ChunkSizeTokens ({sizeTokens}) — {maxOverlapTokens} or less. " +
                "A larger overlap collapses the chunker's slide step, emitting near-duplicate " +
                "chunks (one per character once overlap reaches the chunk size) and one embedding " +
                "request each. If you lowered ChunkSizeTokens for an embedding experiment, lower " +
                "ChunkOverlapTokens to match.");
        }

        _maxChars = sizeTokens * CharsPerToken;
        _overlapChars = overlapTokens * CharsPerToken;
    }

    public IReadOnlyList<TextChunk> Chunk(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return [];

        var text = body.Trim();
        if (text.Length <= _maxChars)
        {
            return [new TextChunk(0, text, EstimateTokens(text))];
        }

        var chunks = new List<TextChunk>();
        var paragraphs = SplitParagraphs(text);

        var current = new System.Text.StringBuilder(_maxChars);
        // Tracks whether `current` holds real paragraph content since the last
        // flush, or only the overlap tail Flush carries forward. We must never
        // emit a chunk that is nothing but that carried-over overlap — it would
        // be a verbatim duplicate of the previous chunk's tail as its own chunk.
        bool hasContent = false;
        foreach (var para in paragraphs)
        {
            // If adding the paragraph would overflow, flush — but only when
            // there's real content to flush (a lone overlap tail keeps
            // accumulating instead of being emitted on its own).
            if (hasContent && current.Length + 2 + para.Length > _maxChars)
            {
                Flush(chunks, current);
                hasContent = false;
            }

            // A single paragraph longer than maxChars must be hard-split.
            if (para.Length > _maxChars)
            {
                if (hasContent) Flush(chunks, current);
                // Drop any carried-over overlap: HardSplit's slices already
                // carry their own internal overlap, and emitting the lone tail
                // here would duplicate the previous chunk's ending.
                current.Clear();
                hasContent = false;
                foreach (var slice in HardSplit(para))
                {
                    chunks.Add(new TextChunk(chunks.Count, slice, EstimateTokens(slice)));
                }
                continue;
            }

            if (current.Length > 0) current.Append("\n\n");
            current.Append(para);
            hasContent = true;
        }
        if (hasContent) Flush(chunks, current);

        return chunks;
    }

    private void Flush(List<TextChunk> chunks, System.Text.StringBuilder current)
    {
        var text = current.ToString();
        chunks.Add(new TextChunk(chunks.Count, text, EstimateTokens(text)));
        current.Clear();

        // Carry forward an overlap window from the tail of the just-flushed chunk
        // so semantic continuity isn't lost across chunk boundaries.
        if (_overlapChars > 0 && text.Length > _overlapChars)
        {
            current.Append(text, text.Length - _overlapChars, _overlapChars);
        }
    }

    private IEnumerable<string> HardSplit(string s)
    {
        // Slide a window with overlap across an unbroken block (e.g. a long URL or wall of text).
        // The Max(1, …) is unreachable now that the constructor caps overlap at
        // half the chunk size — and it is not a substitute for that cap: a step
        // clamped to 1 is precisely the one-chunk-per-character blow-up, so
        // reaching this clamp would already be the bug, not a save from it.
        var step = Math.Max(1, _maxChars - _overlapChars);
        for (int i = 0; i < s.Length; i += step)
        {
            var len = Math.Min(_maxChars, s.Length - i);
            yield return s.Substring(i, len);
            if (i + len >= s.Length) yield break;
        }
    }

    private static List<string> SplitParagraphs(string text)
    {
        var parts = text.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    private static int EstimateTokens(string s) => Math.Max(1, s.Length / CharsPerToken);
}
