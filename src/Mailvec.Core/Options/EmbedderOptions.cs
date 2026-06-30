namespace Mailvec.Core.Options;

public sealed class EmbedderOptions
{
    public const string SectionName = "Embedder";

    public int PollIntervalSeconds { get; set; } = 30;

    // mxbai-embed-large has a 512-token context. Our chunker estimates tokens
    // at 4 chars each, but real BPE on email text can run as low as 1-2
    // chars/token (CJK, dense URLs, base64, marketing-email punctuation). No
    // char-based ceiling is fully safe — OllamaClient catches context-length
    // 400s and split/truncates as a fallback. This default keeps that fallback
    // path rare for typical English mail.
    public int ChunkSizeTokens { get; set; } = 200;
    public int ChunkOverlapTokens { get; set; } = 32;
    public int MaxConcurrentRequests { get; set; } = 2;

    // Below this threshold (chars in the trimmed body), the message's body
    // is not embedded — only its attachments (if any) are. Rationale:
    // very-short bodies (e.g. user replies like "I'm willing to help.")
    // would otherwise produce embeddings dominated by the prepended
    // subject, ranking the message high for any query whose tokens
    // overlap with the subject. The keyword/FTS leg still indexes the
    // body + subject so these messages remain searchable by exact terms,
    // they just stop polluting semantic results. Set to 0 to disable.
    public int MinBodyCharsForVector { get; set; } = 100;

    // OCR for scanned / image-only PDFs (extraction_status='no_text'). When on,
    // the embedder renders each such PDF and transcribes it with the Ollama
    // vision model before embedding, so the content becomes searchable. Heavy
    // but rare; on by default. Degrades gracefully (logs + skips) when the
    // vision model isn't pulled. See docs/contributing/attachment-ocr.md.
    public bool OcrEnabled { get; set; } = true;

    // Scanned PDFs OCR'd per OCR pass before yielding to the embed pass. Small,
    // because OCR is ~tens of seconds per page; keeps the two passes alternating.
    public int OcrBatchSize { get; set; } = 4;

    // Cap pages rendered + OCR'd per PDF — bounds cost on a pathologically long scan.
    public int OcrMaxPagesPerPdf { get; set; } = 20;
}
