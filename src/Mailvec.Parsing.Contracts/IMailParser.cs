using Mailvec.Core.Attachments;
using Mailvec.Core.Parsing;
using Mailvec.Pdf;

namespace Mailvec.Parsing.Contracts;

/// <summary>
/// The one seam through which mail content is parsed. Every operation takes
/// the raw <c>.eml</c> bytes (plus a <c>part_index</c> where a single
/// attachment is meant) and returns plain data — a parsed record, extracted
/// text, decoded bytes, a JPEG. Nothing MimeKit-, PdfPig-, PDFium- or
/// Skia-shaped crosses this boundary, which is what lets the implementation
/// live in a different process from the caller.
///
/// Two implementations: <c>InProcessParser</c> (Mailvec.Parsing — today's code
/// behind the interface; the macOS launchd install) and, from phase 2 of
/// docs/proposals/attachment-parser-isolation.md, a remote client to the
/// <c>parse</c> container. Callers resolve whichever is configured through
/// <c>ParserRegistration.AddMailvecParser</c>; nothing else may choose.
///
/// Which FILE to read is not this interface's concern — the containment guard
/// and the "is the .eml still there?" answer stay with
/// <c>MaildirAttachmentReader</c> in Core. What is INSIDE the file is.
/// </summary>
public interface IMailParser
{
    /// <summary>"inprocess" or "remote" — for health and doctor reporting.</summary>
    string Mode { get; }

    /// <summary>
    /// Headers, body text (HTML converted and reply-trimmed), content hash and
    /// the attachment list. With <paramref name="extractAttachmentText"/> the
    /// attachments also carry extracted text and an <see cref="ExtractionStatus"/>;
    /// without it they carry metadata only (the indexer wants the former, the
    /// backfills the latter).
    /// </summary>
    ParsedMessage ParseMessage(byte[] eml, bool extractAttachmentText);

    /// <summary>Run text extraction for one attachment, as the indexer would have.</summary>
    ExtractionResult ExtractAttachmentText(byte[] eml, int partIndex);

    /// <summary>Resolved (safe) filename and sniffed content type of one part, without decoding it.</summary>
    PartInfo DescribePart(byte[] eml, int partIndex);

    /// <summary>
    /// Decode one part. <paramref name="maxBytes"/> is enforced DURING the
    /// decode (never by trusting a declared size) and has no default anywhere
    /// on this path: the right ceiling differs per caller. Over it,
    /// <see cref="AttachmentTooLargeException"/>.
    /// </summary>
    DecodedPart DecodePart(byte[] eml, int partIndex, long? maxBytes);

    /// <summary>
    /// Rasterise up to <paramref name="maxPages"/> pages of a PDF part starting
    /// at 0-based <paramref name="firstPage"/>, as JPEGs with the long edge
    /// capped at <see cref="RasterLimits.MaxEdgePx"/>. Pages outside the
    /// document are simply absent from the result; <see cref="PdfRender.PageCount"/>
    /// says how many exist. One call renders a whole document for the OCR pass
    /// so the (possibly remote) parser is not sent the PDF once per page.
    /// </summary>
    PdfRender RenderPdfPages(byte[] eml, int partIndex, int firstPage, int maxPages, long? maxBytes);

    /// <summary>
    /// Decode an image part and normalise it for vision (white-flattened,
    /// ≤ <see cref="RasterLimits.MaxEdgePx"/>, JPEG). Null when the bytes are
    /// not a decodable raster or exceed the decompression-bomb ceiling.
    /// </summary>
    NormalizedImage? NormalizeImage(byte[] eml, int partIndex, long? maxBytes);

    /// <summary>
    /// The body-text derivation the indexer applies to an HTML body, for
    /// <c>mailvec rebuild-bodies</c>: HTML → text, then reply trimming.
    /// </summary>
    string? BodyTextFromHtml(string html, string? subject);
}

/// <summary>Resolved filename and content type of a part (see <c>AttachmentNaming</c>).</summary>
public sealed record PartInfo(string FileName, string ContentType);

/// <summary>A decoded part: resolved name and type plus the bytes.</summary>
public sealed record DecodedPart(string FileName, string ContentType, byte[] Bytes);

/// <summary>Page count of the PDF and the JPEGs for the requested range (possibly fewer than asked).</summary>
public sealed record PdfRender(int PageCount, IReadOnlyList<byte[]> Pages);

/// <summary>
/// What an in-process parser needs from configuration. Built by
/// <c>ParserRegistration</c> in Core (from <c>Indexer:AttachmentMaxBytes</c>)
/// and handed to the host's factory, so Core never names the implementation
/// type and Mailvec.Parsing never reads configuration.
/// </summary>
public sealed record InProcessParserSettings(long AttachmentMaxBytes);

/// <summary>Limits shared by the rasterisers and the callers that describe their output.</summary>
public static class RasterLimits
{
    /// <summary>
    /// Long-edge ceiling in pixels, just under Claude's ~1568px image cap.
    /// Rendering larger wastes payload Claude would only throw away.
    /// </summary>
    public const int MaxEdgePx = 1536;
}

/// <summary>
/// How a parse failed, for callers that must decide between "retry", "retire
/// the document" and "the parser is down". Mirrors <c>VisionFailureKind</c>:
/// an unclassified failure maps to <see cref="Crashed"/> (retry with strikes),
/// never to <see cref="DocumentRejected"/> (retire) — a bug in classification
/// must retry a document, not destroy it.
/// </summary>
public enum ParseFailureKind
{
    /// <summary>The parser is unreachable or restarting. Never a strike against a document.</summary>
    Unavailable,

    /// <summary>The parser opened the document and refused it: encrypted, corrupt, not the claimed format.</summary>
    DocumentRejected,

    /// <summary>The parser died, hung or errored on this document.</summary>
    Crashed,
}

/// <summary>A classified parse failure, thrown by the remote parser (phase 2).</summary>
public sealed class ParseException(ParseFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ParseFailureKind Kind { get; } = kind;
}
