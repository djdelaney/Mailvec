using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Mailvec.Core.Attachments;
using Mailvec.Parsing.Contracts;
using Mailvec.Pdf;

namespace Mailvec.Core.Parsing;

/// <summary>
/// <see cref="IMailParser"/> over HTTP to the <c>parse</c> host
/// (<see cref="ParserWire"/>). The container deployment's parser: this process
/// reads the <c>.eml</c> bytes and ships them; every MimeKit / PdfPig / PDFium
/// call happens in a container that holds no volumes, no secrets and no egress.
///
/// <para><b>Synchronous on purpose.</b> <see cref="IMailParser"/> is synchronous
/// because its callers are (the scanner, the MCP tool methods, the OCR pass's
/// render step), and <see cref="HttpClient.Send(HttpRequestMessage, HttpCompletionOption)"/>
/// is a genuinely synchronous send on SocketsHttpHandler — no sync-over-async,
/// no thread-pool starvation under a burst of tool calls.</para>
///
/// <para><b>Failure classification is the point of this class.</b> Every
/// non-2xx answer and every transport failure is mapped either to the SAME
/// exception type the in-process parser would have thrown (so the MCP tools
/// and the OCR pass keep branching on <see cref="ArgumentOutOfRangeException"/>
/// and <see cref="AttachmentTooLargeException"/> unchanged) or to a
/// <see cref="ParseException"/> whose <see cref="ParseFailureKind"/> says
/// whether to retire the document, count a strike, or wait for the service.
/// Anything unclassified maps to <see cref="ParseFailureKind.Crashed"/> (retry
/// with strikes), never to <see cref="ParseFailureKind.DocumentRejected"/>
/// (retire) — the same rule as <c>VisionFailureKind</c>.</para>
/// </summary>
public sealed class RemoteParser(Func<HttpClient> clientFactory) : IMailParser
{
    /// <summary>The named <see cref="HttpClient"/> <c>ParserRegistration</c> configures for this class.</summary>
    public const string HttpClientName = "mailvec-parser";

    public string Mode => ParserRegistration.RemoteMode;

    public ParsedMessage ParseMessage(byte[] eml, bool extractAttachmentText) =>
        PostEml<ParsedMessage>(Query(ParserWire.Message, ("extractText", extractAttachmentText ? "true" : "false")), eml)!;

    public ExtractionResult ExtractAttachmentText(byte[] eml, int partIndex) =>
        PostEml<ExtractionResult>(ParserWire.PartText(partIndex), eml)!;

    public PartInfo DescribePart(byte[] eml, int partIndex) =>
        PostEml<PartInfo>(ParserWire.Part(partIndex), eml)!;

    public DecodedPart DecodePart(byte[] eml, int partIndex, long? maxBytes) =>
        PostEml<DecodedPart>(Query(ParserWire.PartBytes(partIndex), ("maxBytes", maxBytes)), eml)!;

    public PdfRender RenderPdfPages(byte[] eml, int partIndex, int firstPage, int maxPages, long? maxBytes)
    {
        var render = PostEml<PdfRender>(Query(ParserWire.PartPdfPages(partIndex),
            ("first", firstPage), ("max", maxPages), ("maxBytes", maxBytes)), eml)!;
        // Shape guard covers the count in bulk; this is the per-operation
        // invariant — we asked for at most maxPages, and a service that
        // answers with more is not our service.
        if (render.Pages.Count > Math.Max(0, maxPages) || render.PageCount < 0)
            throw new ParseException(ParseFailureKind.Crashed,
                $"The parse service returned {render.Pages.Count} page(s) where at most {maxPages} were requested.");
        return render;
    }

    public NormalizedImage? NormalizeImage(byte[] eml, int partIndex, long? maxBytes) =>
        PostEml<NormalizedImage>(Query(ParserWire.PartImage(partIndex), ("maxBytes", maxBytes)), eml, allowNoContent: true);

    public string? BodyTextFromHtml(string html, string? subject)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ParserWire.Html)
        {
            Content = JsonContent.Create(new HtmlRequest(html, subject), options: ParserWire.Json),
        };
        return Send<HtmlResponse>(request)!.Text;
    }

    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        // Bounded independently of the client's parse timeout: /health is the
        // mcp container's compose healthcheck with a 10 s budget of its own.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var client = clientFactory();
            using var response = await client.GetAsync(ParserWire.Up.TrimStart('/'), linked.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    private static string Query(string path, params (string Name, object? Value)[] parameters)
    {
        var parts = parameters
            .Where(p => p.Value is not null)
            .Select(p => $"{p.Name}={Uri.EscapeDataString(Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture)!)}")
            .ToList();
        return parts.Count == 0 ? path : $"{path}?{string.Join('&', parts)}";
    }

    private T? PostEml<T>(string pathAndQuery, byte[] eml, bool allowNoContent = false)
    {
        ArgumentNullException.ThrowIfNull(eml);
        using var request = new HttpRequestMessage(HttpMethod.Post, pathAndQuery)
        {
            Content = new ByteArrayContent(eml),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ParserWire.EmlContentType);
        return Send<T>(request, allowNoContent);
    }

    private T? Send<T>(HttpRequestMessage request, bool allowNoContent = false)
    {
        using var client = clientFactory();
        HttpResponseMessage response;
        try
        {
            response = client.Send(request, HttpCompletionOption.ResponseContentRead);
        }
        catch (HttpRequestException ex)
        {
            throw Classify(ex);
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient.Timeout elapsed with no answer at all. The host's own
            // per-request timeout is shorter and answers 504 (→ Crashed), so
            // reaching THIS one means the service isn't responding: wait for
            // it, don't blame the document.
            throw new ParseException(ParseFailureKind.Unavailable,
                "The parse service did not answer within the client timeout.", ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    if (allowNoContent) return default;
                    throw new ParseException(ParseFailureKind.Crashed, "The parse service returned no content where a result was required.");
                }
                // Already buffered (ResponseContentRead, under MaxResponseBytes),
                // so this is the buffer, not a second read. The shape pre-scan
                // runs over it BEFORE deserialization — the byte ceiling bounds
                // the wire, ResponseShape bounds what materializing it costs.
                var body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                ResponseShape.Check(body);
                T? result;
                try
                {
                    result = JsonSerializer.Deserialize<T>(body, ParserWire.Json);
                }
                catch (JsonException ex)
                {
                    // A 200 whose body is not our JSON — malformed, or (with
                    // ParserWire.Json's required/nullable enforcement) missing
                    // a member the record declares — describes the service,
                    // never the document. An unclassified exception is read by
                    // every caller as a document verdict, i.e. a permanent
                    // retirement of a healthy attachment.
                    throw new ParseException(ParseFailureKind.Crashed,
                        "The parse service answered with a body that is not a parse result.", ex);
                }
                return result
                    ?? throw new ParseException(ParseFailureKind.Crashed, "The parse service returned an empty result.");
            }

            throw MapError(response.StatusCode, TryReadError(response));
        }
    }

    private static ParseError? TryReadError(HttpResponseMessage response)
    {
        try
        {
            if (response.Content.Headers.ContentLength == 0) return null;
            using var stream = response.Content.ReadAsStream();
            return JsonSerializer.Deserialize<ParseError>(stream, ParserWire.Json);
        }
        catch (JsonException)
        {
            return null; // not our body — a proxy or Kestrel's own error page
        }
    }

    private static Exception MapError(HttpStatusCode status, ParseError? error)
    {
        var message = error?.Message ?? $"The parse service answered {(int)status}.";
        switch ((int)status)
        {
            case 404 when error?.Type == ParseErrorTypes.PartOutOfRange:
                return new ArgumentOutOfRangeException("partIndex", error.Message);
            case 413 when error?.Type == ParseErrorTypes.AttachmentTooLarge:
                return new AttachmentTooLargeException(error.Describe ?? "The attachment", error.LimitBytes ?? 0);
            case 413:
                // Kestrel's request-body cap, or our RequestTooLarge body: the
                // whole .eml is bigger than the service accepts. Deterministic
                // for this message.
                return new ParseException(ParseFailureKind.DocumentRejected,
                    error?.Message ?? "The message is larger than the parse service accepts.");
            case 422:
                return new ParseException(ParseFailureKind.DocumentRejected, message);
            case 503:
                return new ParseException(ParseFailureKind.Unavailable, message);
            case 504:
                return new ParseException(ParseFailureKind.Crashed,
                    error?.Message ?? "The parse service timed out on this document and is restarting.");
            case >= 500:
                return new ParseException(ParseFailureKind.Crashed, message);
            case >= 300 and < 400:
                // Never followed (ParserHttp turns automatic redirects off):
                // the body a redirect would resend is the whole .eml, to a
                // destination the service chose. A service that redirects is
                // not our service.
                return new ParseException(ParseFailureKind.Crashed,
                    $"The parse service answered {(int)status} with a redirect, which is refused.");
            default:
                // A 4xx we don't speak: a contract mismatch between this client
                // and the host, i.e. a deployment bug, not a document property
                // — so Crashed (retry with strikes), never an unclassified
                // exception the callers would retire the document on.
                return new ParseException(ParseFailureKind.Crashed,
                    $"The parse service rejected the request: {(int)status} {message}");
        }
    }

    private static ParseException Classify(HttpRequestException ex) => ex.HttpRequestError switch
    {
        // Nobody answered: the container is down, restarting, or not resolvable.
        HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError
            => new ParseException(ParseFailureKind.Unavailable, $"The parse service is unreachable: {ex.Message}", ex),

        // Somebody answered and then the connection died: the host went away
        // mid-request, which is what a parser crash or OOM kill looks like
        // from here.
        _ => new ParseException(ParseFailureKind.Crashed, $"The parse service failed mid-request: {ex.Message}", ex),
    };
}
