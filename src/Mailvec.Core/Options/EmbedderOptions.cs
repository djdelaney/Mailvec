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

    // OCR for image *attachments* (image/jpeg, image/png, …) the indexer left at
    // 'unsupported'. Same vision pipeline as the scanned-PDF pass, behind a
    // two-stage gate that keeps it off the corpus of logos / signature icons /
    // tracking pixels that dominate inline images: a cheap byte pre-filter in
    // SQL (ImageOcrMinBytes), then a post-decode dimension/aspect gate. On a real
    // corpus the byte gate alone sheds the entire sub-2KB icon-strip population;
    // the decode gate catches byte-heavy-but-tiny images and banner strips. GIFs
    // are excluded outright (near-always animated/decorative). See
    // docs/contributing/attachment-ocr.md.
    public bool ImageOcrEnabled { get; set; } = true;

    // Stage 1 (pre-render, in SQL): skip image attachments smaller than this.
    // Logos/signature decoration/tracking pixels sit well under 25KB; document
    // photos, screenshots, and scans sit well above 50KB. 50KB is the
    // conservative floor — raise it to OCR fewer, lower it to OCR more.
    public long ImageOcrMinBytes { get; set; } = 50 * 1024;

    /// <summary>
    /// Minimum characters an OCR result must contain to be stored as recovered
    /// text. Below it, the document is treated exactly as if OCR had returned
    /// nothing — the same terminal state a genuinely textless image gets.
    /// </summary>
    /// <remarks>
    /// Vision models return a stray glyph or two off photographs of physical
    /// objects, and without a floor that becomes indexed content. Observed on a
    /// real corpus: a 2.8 MB phone photo of children's placemats and books OCR'd
    /// to <c>1.1</c> — three characters, presumably off a book spine — and was
    /// written back as status='ocr' with <c>indexedForSearch: true</c>, joining
    /// the FTS <c>attachment_text</c> column and getting its own embedded chunk.
    /// Its near-identical sibling photo in the same email correctly came back
    /// empty and was marked 'no_text'. The status is terminal either way, so
    /// nothing revisits the junk.
    ///
    /// This is the OCR analogue of <see cref="MinBodyCharsForVector"/>, which
    /// exists for the same reason on the body path.
    ///
    /// The trade is real and accepted: a genuinely short result — a door number,
    /// a receipt total — is discarded too. 10 is chosen because a photo that
    /// contains real text almost never yields ONLY a few characters; it yields
    /// the surrounding text as well. Set 0 to store whatever comes back.
    /// </remarks>
    public int OcrMinTextChars { get; set; } = 10;

    /// <summary>
    /// Ceiling on the decoded size of an attachment the OCR passes will pull
    /// out of the Maildir. Over it, the document is marked 'failed' and not
    /// retried — the size won't change.
    /// </summary>
    /// <remarks>
    /// Nothing upstream guarantees this on its own: the PDF pass selects on
    /// extraction_status='no_text', and the image pass on 'unsupported', both
    /// of which say what the INDEXER concluded, not how big the bytes are. 25 MB
    /// matches Indexer:AttachmentMaxBytes so the OCR pass doesn't decode what
    /// the extractor already declined for size.
    /// </remarks>
    public long OcrMaxAttachmentBytes { get; set; } = 25 * 1024 * 1024;

    // Stage 2 (post-decode): skip images whose *smaller* pixel dimension is below
    // this — icons and avatars that slipped past the byte gate. 200px is below
    // any legible page of text but above every icon.
    public int ImageOcrMinDimension { get; set; } = 200;

    // Stage 2 (post-decode): skip images whose long/short edge ratio exceeds this
    // — banner strips and 1×N spacer rows that carry no readable text.
    public double ImageOcrMaxAspectRatio { get; set; } = 8.0;
}
