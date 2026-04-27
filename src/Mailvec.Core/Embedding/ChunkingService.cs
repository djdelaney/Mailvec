using Mailvec.Core.Options;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Embedding;

public sealed record TextChunk(int Index, string Text, int EstimatedTokenCount);

/// <summary>
/// Splits a message body into overlapping chunks sized for the embedding
/// model's context window. Token counts are estimated at ~4 chars/token.
/// Prefers paragraph/sentence breaks; falls back to character-level cuts for
/// long unbroken text. Short messages return a single chunk.
/// </summary>
public sealed class ChunkingService(IOptions<EmbedderOptions> options)
{
    private const int CharsPerToken = 4;

    private readonly int _maxChars = Math.Max(1, options.Value.ChunkSizeTokens) * CharsPerToken;
    private readonly int _overlapChars = Math.Max(0, options.Value.ChunkOverlapTokens) * CharsPerToken;

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
        foreach (var para in paragraphs)
        {
            // If adding the paragraph would overflow, flush.
            if (current.Length > 0 && current.Length + 2 + para.Length > _maxChars)
            {
                Flush(chunks, current);
            }

            // A single paragraph longer than maxChars must be hard-split.
            if (para.Length > _maxChars)
            {
                if (current.Length > 0) Flush(chunks, current);
                foreach (var slice in HardSplit(para))
                {
                    chunks.Add(new TextChunk(chunks.Count, slice, EstimateTokens(slice)));
                }
                continue;
            }

            if (current.Length > 0) current.Append("\n\n");
            current.Append(para);
        }
        if (current.Length > 0) Flush(chunks, current);

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
