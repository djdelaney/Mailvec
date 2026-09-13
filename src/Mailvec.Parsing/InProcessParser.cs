using System.Runtime.Versioning;
using Mailvec.Core.Attachments;
using Mailvec.Core.Parsing;
using Mailvec.Parsing.Contracts;
using Mailvec.Pdf;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Mailvec.Parsing;

/// <summary>
/// <see cref="IMailParser"/> over the parsers in this assembly, in the calling
/// process. Byte-for-byte the pre-seam code path: <see cref="MessageParser"/>,
/// <see cref="AttachmentTextExtractor"/>, <see cref="MimePartDecoder"/>,
/// <see cref="PdfRenderer"/> and <see cref="ImageRenderer"/>, called directly.
/// This is what the macOS launchd install runs, and what every existing test
/// exercises. The container deployment switches to the remote implementation
/// (phase 2) and strips this assembly's dependencies from the privileged
/// binaries' directories, so <c>Parser:Mode=inprocess</c> there fails loudly.
/// </summary>
public sealed class InProcessParser : IMailParser
{
    private readonly AttachmentTextExtractor? _extractor;
    private readonly MessageParser _withText;
    private static readonly MessageParser MetadataOnly = new();

    /// <summary>The production constructor: settings from <c>ParserRegistration</c>, a logger for the extractor.</summary>
    public InProcessParser(InProcessParserSettings settings, ILoggerFactory loggerFactory)
        : this(new AttachmentTextExtractor(
            settings.AttachmentMaxBytes,
            loggerFactory.CreateLogger<AttachmentTextExtractor>()))
    {
    }

    /// <summary>
    /// Test-shaped constructor, mirroring <see cref="MessageParser"/>'s two ctors:
    /// pass null for a parser that never extracts attachment text (the old
    /// <c>new MessageParser()</c>), or a hand-built extractor.
    /// </summary>
    public InProcessParser(AttachmentTextExtractor? extractor)
    {
        _extractor = extractor;
        _withText = new MessageParser(extractor);
    }

    public string Mode => "inprocess";

    public ParsedMessage ParseMessage(byte[] eml, bool extractAttachmentText)
    {
        var mime = MimePartDecoder.Load(eml);
        return (extractAttachmentText ? _withText : MetadataOnly).Parse(mime, eml.LongLength);
    }

    public ExtractionResult ExtractAttachmentText(byte[] eml, int partIndex)
    {
        if (_extractor is null)
            throw new InvalidOperationException("This parser was built without an AttachmentTextExtractor.");

        var entity = MimePartDecoder.Locate(MimePartDecoder.Load(eml), partIndex);
        // Same name / type / size the indexer would have passed: the raw
        // (normalised, not path-safe) name and the DECODED length. The CLI
        // backfills used to pass stored column values here, which were these
        // same facts recorded at index time.
        var rawName = entity.ContentDisposition?.FileName ?? entity.ContentType?.Name;
        var fileName = string.IsNullOrWhiteSpace(rawName) ? null : rawName;
        var contentType = entity.ContentType?.MimeType;
        long? size = entity is MimePart part && part.Content is not null
            ? MessageParser.DecodedContentLength(part.Content)
            : null;
        return _extractor.Extract(entity, fileName, contentType, size);
    }

    public PartInfo DescribePart(byte[] eml, int partIndex) => MimePartDecoder.Describe(eml, partIndex);

    public DecodedPart DecodePart(byte[] eml, int partIndex, long? maxBytes) =>
        MimePartDecoder.Decode(eml, partIndex, maxBytes);

    public PdfRender RenderPdfPages(byte[] eml, int partIndex, int firstPage, int maxPages, long? maxBytes)
    {
        // The renderer is native and platform-gated; the inline OS check both
        // satisfies CA1416 and short-circuits on an unsupported platform.
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            return RenderCore(MimePartDecoder.Decode(eml, partIndex, maxBytes).Bytes, firstPage, maxPages);
        throw new PlatformNotSupportedException("PDF rasterisation is supported on macOS, Linux and Windows only.");
    }

    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    private static PdfRender RenderCore(byte[] pdf, int firstPage, int maxPages)
    {
        int pageCount = PdfRenderer.PageCount(pdf);
        int first = Math.Max(0, firstPage);
        int end = Math.Min(pageCount, first + Math.Max(0, maxPages));
        var pages = new List<byte[]>(Math.Max(0, end - first));
        for (int p = first; p < end; p++)
            pages.Add(PdfRenderer.RenderPageJpeg(pdf, p));
        return new PdfRender(pageCount, pages);
    }

    public NormalizedImage? NormalizeImage(byte[] eml, int partIndex, long? maxBytes)
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            return NormalizeCore(MimePartDecoder.Decode(eml, partIndex, maxBytes).Bytes);
        throw new PlatformNotSupportedException("Image decoding is supported on macOS, Linux and Windows only.");
    }

    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    private static NormalizedImage? NormalizeCore(byte[] image) => ImageRenderer.TryNormalize(image);

    public string? BodyTextFromHtml(string html, string? subject)
    {
        // The same two steps MessageParser.Parse applies to an HTML body.
        var text = HtmlToText.Convert(html);
        if (!string.IsNullOrEmpty(text))
            text = ReplyTrimmer.Trim(text, subject);
        return text;
    }
}
