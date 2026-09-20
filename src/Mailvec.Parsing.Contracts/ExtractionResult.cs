namespace Mailvec.Core.Attachments;

/// <summary>Recovered text (null when none) plus an <see cref="ExtractionStatus"/> value.</summary>
public sealed record ExtractionResult(string? Text, string Status);

/// <summary>
/// The stable <c>attachments.extraction_status</c> enum. Persisted verbatim
/// and surfaced through <c>get_email</c>'s <c>AttachmentInfo.ExtractionStatus</c>,
/// so these strings are a wire contract. Lives in Contracts because both the
/// parser (which produces them) and Core (which stores and queries them) need
/// them, and neither may reference the other.
/// </summary>
public static class ExtractionStatus
{
    /// <summary>Native text extraction succeeded.</summary>
    public const string Done = "done";

    /// <summary>A supported format that yielded no text (a scanned PDF).</summary>
    public const string NoText = "no_text";

    /// <summary>Recovered later by the embedder's vision OCR pass.</summary>
    public const string Ocr = "ocr";

    /// <summary>A format we don't extract from (images, zips, …).</summary>
    public const string Unsupported = "unsupported";

    /// <summary>Exceeds <c>Indexer:AttachmentMaxBytes</c>; not attempted.</summary>
    public const string Oversize = "oversize";

    /// <summary>Password-protected document.</summary>
    public const string Encrypted = "encrypted";

    /// <summary>The parser threw. Terminal: nothing re-selects a failed row.</summary>
    public const string Failed = "failed";
}
