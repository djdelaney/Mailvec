using Mailvec.Core.Attachments;
using Mailvec.Parsing.Contracts;
using Mailvec.Pdf;

namespace Mailvec.Parse;

/// <summary>
/// One route per <see cref="IMailParser"/> operation (<see cref="ParserWire"/>),
/// all through <see cref="Execute{T}"/>, which is where the host's three rules
/// live: every parse runs under the request timeout and the host exits if it
/// overruns; every exception the parser throws is mapped to a status + JSON
/// <see cref="ParseError"/> the client can turn back into the same exception
/// type; every answered request counts toward the exit budget.
/// </summary>
internal static class ParseEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(ParserWire.Up, (RequestBudget budget) =>
            Results.Json(new { status = "ok", mode = "parse", requests = budget.Completed, stopping = budget.Stopping }, ParserWire.Json));

        app.MapPost(ParserWire.Message, (HttpContext ctx, IMailParser parser, bool extractText = false) =>
            RunWithEml(ctx, eml => parser.ParseMessage(eml, extractText)));

        app.MapPost("/v1/parts/{index:int}", (HttpContext ctx, IMailParser parser, int index) =>
            RunWithEml(ctx, eml => parser.DescribePart(eml, index)));

        app.MapPost("/v1/parts/{index:int}/text", (HttpContext ctx, IMailParser parser, int index) =>
            RunWithEml(ctx, eml => parser.ExtractAttachmentText(eml, index)));

        app.MapPost("/v1/parts/{index:int}/bytes", (HttpContext ctx, IMailParser parser, int index, long? maxBytes) =>
            RunWithEml(ctx, eml => parser.DecodePart(eml, index, maxBytes)));

        app.MapPost("/v1/parts/{index:int}/pdf-pages", (HttpContext ctx, IMailParser parser, int index, int first, int max, long? maxBytes) =>
            RunWithEml(ctx, eml => parser.RenderPdfPages(eml, index, first, max, maxBytes)));

        app.MapPost("/v1/parts/{index:int}/image", (HttpContext ctx, IMailParser parser, int index, long? maxBytes) =>
            RunWithEml<NormalizedImage?>(ctx, eml => parser.NormalizeImage(eml, index, maxBytes), nullIsNoContent: true));

        app.MapPost(ParserWire.Html, (HttpContext ctx, IMailParser parser, HtmlRequest request) =>
            Execute(ctx, () => new HtmlResponse(parser.BodyTextFromHtml(request.Html, request.Subject))));
    }

    private static async Task<IResult> RunWithEml<T>(HttpContext ctx, Func<byte[], T> work, bool nullIsNoContent = false)
    {
        byte[] eml;
        try
        {
            eml = await ReadBodyAsync(ctx);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            var options = ctx.RequestServices.GetRequiredService<ParseHostOptions>();
            return Error(StatusCodes.Status413PayloadTooLarge, ParseErrorTypes.RequestTooLarge,
                $"The message is larger than the parse service accepts ({options.MaxRequestBodyBytes / (1024 * 1024)} MB).");
        }
        return await Execute(ctx, () => work(eml), nullIsNoContent);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext ctx)
    {
        var length = ctx.Request.ContentLength;
        using var ms = length is > 0 and < int.MaxValue ? new MemoryStream((int)length.Value) : new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
        return ms.ToArray();
    }

    private static async Task<IResult> Execute<T>(HttpContext ctx, Func<T> work, bool nullIsNoContent = false)
    {
        var options = ctx.RequestServices.GetRequiredService<ParseHostOptions>();
        var budget = ctx.RequestServices.GetRequiredService<RequestBudget>();
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Mailvec.Parse");

        // The parsers take no cancellation token, so the work runs on its own
        // thread and the request merely stops WAITING for it. An overrun is a
        // property of the document (phase 0: a 958-byte PDF that renders for
        // as long as its author likes), and the only way to reclaim the thread
        // is to let the process die: answer 504 so the caller can classify it
        // as a strike against that document, then exit.
        var task = Task.Run(work);
        var completed = await Task.WhenAny(task, Task.Delay(options.RequestTimeout, ctx.RequestAborted));
        if (completed != task)
        {
            logger.LogError("parse: {Path} exceeded the {Timeout}s request timeout; answering 504 and exiting so the parse thread is reclaimed.",
                ctx.Request.Path, options.RequestTimeoutSeconds);
            budget.StopAfterResponse(ctx, "a parse exceeded Parser:RequestTimeoutSeconds");
            return Error(StatusCodes.Status504GatewayTimeout, ParseErrorTypes.Timeout,
                $"The parse exceeded the service's {options.RequestTimeoutSeconds}s timeout; the service is restarting.");
        }

        try
        {
            var result = await task;
            budget.RequestCompleted(ctx);
            if (result is null && nullIsNoContent) return Results.NoContent();
            return Results.Json(result, ParserWire.Json);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            budget.RequestCompleted(ctx);
            return Error(StatusCodes.Status404NotFound, ParseErrorTypes.PartOutOfRange, ex.Message);
        }
        catch (AttachmentTooLargeException ex)
        {
            budget.RequestCompleted(ctx);
            return Error(StatusCodes.Status413PayloadTooLarge, ParseErrorTypes.AttachmentTooLarge, ex.Message, ex.Describe, ex.LimitBytes);
        }
        catch (Exception ex)
        {
            // The parser refused or choked on this document: corrupt, encrypted,
            // not the claimed format, a MIME structure MimeKit won't read. The
            // type name is what the operator needs in the log; the message
            // (which can quote document bytes) travels only to our own caller
            // over the internal network, and the MCP tools already replace it
            // with stable text before anything reaches a remote client.
            budget.RequestCompleted(ctx);
            logger.LogWarning("parse: {Path} rejected the document: {Type}", ctx.Request.Path, ex.GetType().Name);
            return Error(StatusCodes.Status422UnprocessableEntity, ParseErrorTypes.DocumentRejected,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IResult Error(int status, string type, string message, string? describe = null, long? limitBytes = null) =>
        Results.Json(new ParseError(type, message, describe, limitBytes), ParserWire.Json, statusCode: status);
}
