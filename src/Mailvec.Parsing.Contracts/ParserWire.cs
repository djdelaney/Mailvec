using System.Text.Json;

namespace Mailvec.Parsing.Contracts;

/// <summary>
/// The HTTP shape of <see cref="IMailParser"/> between a caller (<c>RemoteParser</c>
/// in Core) and the <c>parse</c> host (<c>Mailvec.Parse</c>). Both sides
/// compile against this file, so a route or field can't drift on one side
/// only. Every operation is a POST whose body is the raw <c>.eml</c> bytes
/// (<see cref="EmlContentType"/>) and whose parameters ride in the route and
/// query string; results come back as JSON of the Contracts records
/// (<c>byte[]</c> fields as base64).
/// </summary>
public static class ParserWire
{
    public const string EmlContentType = "application/octet-stream";

    /// <summary>Liveness: 200 with a small JSON body. The compose healthcheck polls it.</summary>
    public const string Up = "/up";

    /// <summary>POST, query <c>extractText=true|false</c> → <c>ParsedMessage</c>.</summary>
    public const string Message = "/v1/message";

    /// <summary>POST → <c>PartInfo</c>.</summary>
    public static string Part(int partIndex) => $"/v1/parts/{partIndex}";

    /// <summary>POST → <c>ExtractionResult</c>.</summary>
    public static string PartText(int partIndex) => $"/v1/parts/{partIndex}/text";

    /// <summary>POST, query <c>maxBytes</c> → <c>DecodedPart</c>.</summary>
    public static string PartBytes(int partIndex) => $"/v1/parts/{partIndex}/bytes";

    /// <summary>POST, query <c>first</c>, <c>max</c>, <c>maxBytes</c> → <c>PdfRender</c>.</summary>
    public static string PartPdfPages(int partIndex) => $"/v1/parts/{partIndex}/pdf-pages";

    /// <summary>POST, query <c>maxBytes</c> → <c>NormalizedImage</c>, or 204 for "not a decodable image".</summary>
    public static string PartImage(int partIndex) => $"/v1/parts/{partIndex}/image";

    /// <summary>POST, JSON <see cref="HtmlRequest"/> → <see cref="HtmlResponse"/>.</summary>
    public const string Html = "/v1/html";

    /// <summary>
    /// One serializer configuration for both ends. Web defaults: camelCase,
    /// case-insensitive, records bound through their constructors.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// The error body the host returns with any non-2xx status, so the client can
/// rethrow the SAME exception type the in-process parser would have thrown —
/// the MCP tools and the OCR pass branch on those types.
/// </summary>
public sealed record ParseError(string Type, string Message, string? Describe = null, long? LimitBytes = null);

/// <summary>Values of <see cref="ParseError.Type"/>, paired with the status the host sends them under.</summary>
public static class ParseErrorTypes
{
    /// <summary>404 — <c>ArgumentOutOfRangeException</c> in-process.</summary>
    public const string PartOutOfRange = "part_out_of_range";

    /// <summary>413 — <c>AttachmentTooLargeException</c> in-process; carries Describe + LimitBytes.</summary>
    public const string AttachmentTooLarge = "attachment_too_large";

    /// <summary>413 — the whole <c>.eml</c> exceeds the host's request body cap.</summary>
    public const string RequestTooLarge = "request_too_large";

    /// <summary>422 — the parser threw on this document (corrupt, encrypted, not the claimed format).</summary>
    public const string DocumentRejected = "document_rejected";

    /// <summary>504 — the parse exceeded the host's timeout; the host exits after answering.</summary>
    public const string Timeout = "timeout";
}

public sealed record HtmlRequest(string Html, string? Subject);

public sealed record HtmlResponse(string? Text);
