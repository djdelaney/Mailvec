namespace Mailvec.Core.Models;

/// <summary>
/// Per-attachment row from the attachments table. <see cref="Id"/> is the
/// internal SQLite id (zero on parse output, populated after persistence) so
/// search results can join back to attachments.
///
/// Extracted text fields capture the result of running attachment content
/// through <c>AttachmentTextExtractor</c>. <c>ExtractionStatus</c> is one of
/// the constants on <c>AttachmentTextExtractor</c> (or null when extraction
/// hasn't been attempted yet).
/// </summary>
public sealed record Attachment(
    int PartIndex,
    string? FileName,
    string? ContentType,
    long? SizeBytes,
    long Id = 0,
    string? ExtractedText = null,
    string? ExtractionStatus = null,
    DateTimeOffset? ExtractedAt = null,
    // Length of extracted_text without loading the text itself — populated by
    // the summary loader (thread view) via SQL LENGTH(). Null when there is no
    // text. When ExtractedText IS loaded, prefer its .Length; SQL LENGTH()
    // counts code points vs UTF-16 units, close enough for paging estimates.
    int? ExtractedTextChars = null);
