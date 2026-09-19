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
        var gate = ctx.RequestServices.GetRequiredService<ParseGate>();
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Mailvec.Parse");

        // Admission control (ParseGate). Full is not a fault of the document
        // or the service: 503, which the caller classifies as Unavailable and
        // waits out. A caller that gives up while queued just leaves.
        bool admitted;
        try
        {
            admitted = await gate.TryEnterAsync(options.RequestTimeout, ctx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        if (!admitted)
        {
            logger.LogWarning("parse: {Path} could not get one of {Slots} parse slot(s) within {Timeout}s; answering 503.",
                ctx.Request.Path, gate.Slots, options.RequestTimeoutSeconds);
            return Error(StatusCodes.Status503ServiceUnavailable, ParseErrorTypes.Busy,
                $"The parse service is at its concurrency limit ({gate.Slots}); retry shortly.");
        }

        // The parsers take no cancellation token, so the work runs on its own
        // thread and the request merely stops WAITING for it. An overrun is a
        // property of the document (phase 0: a 958-byte PDF that renders for
        // as long as its author likes), and the only way to reclaim the thread
        // is to let the process die: answer 504 so the caller can classify it
        // as a strike against that document, then exit.
        //
        // The wait is NOT tied to RequestAborted. A caller that disconnects
        // (an indexer restart, a cancelled tool call) says nothing about the
        // document: the parse is left to finish within the same timeout, its
        // slot released when it does, and only a genuine overrun exits the
        // host. Tying the two together made every client disconnect a host
        // restart for every other caller.
        Task<T> task;
        try
        {
            task = Task.Run(() => { try { return work(); } finally { gate.Exit(); } });
        }
        catch (Exception ex)
        {
            // The task never started (thread injection refused under
            // pids_limit, say), so its finally never runs: give the slot back
            // here or four such failures leave the host answering 503 to
            // everything forever — with /up green, since 503s don't count
            // toward the exit budget.
            gate.Exit();
            logger.LogError(ex, "parse: {Path} could not start a parse thread.", ctx.Request.Path);
            return Error(StatusCodes.Status503ServiceUnavailable, ParseErrorTypes.Busy,
                "The parse service could not start a parse thread; retry shortly.");
        }
        var completed = await Task.WhenAny(task, Task.Delay(options.RequestTimeout));
        if (completed != task)
        {
            logger.LogError("parse: {Path} exceeded the {Timeout}s request timeout{Detail}; answering 504 and exiting so the parse thread is reclaimed.",
                ctx.Request.Path, options.RequestTimeoutSeconds,
                ctx.RequestAborted.IsCancellationRequested ? " (the caller had already disconnected)" : "");
            budget.StopAfterResponse(ctx, "a parse exceeded Parser:RequestTimeoutSeconds");
            return Error(StatusCodes.Status504GatewayTimeout, ParseErrorTypes.Timeout,
                $"The parse exceeded the service's {options.RequestTimeoutSeconds}s timeout; the service is restarting.");
        }

        if (ctx.RequestAborted.IsCancellationRequested)
        {
            // Finished within the timeout after the caller left: nothing to
            // answer, nothing wrong with the host, the slot is already free.
            // Counts toward the budget like any other completed parse.
            budget.RequestCompleted(ctx);
            try
            {
                await task;
                logger.LogInformation("parse: {Path} completed after its caller disconnected; result discarded.", ctx.Request.Path);
            }
            catch (Exception ex)
            {
                // Nobody is waiting for the verdict, but the operator still
                // gets the log line every other failure path writes.
                logger.LogWarning("parse: {Path} failed after its caller disconnected: {Type}: {Message}",
                    ctx.Request.Path, ex.GetType().Name, ex.Message);
            }
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
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
            //
            // An accepted asymmetry, stated rather than hidden: EVERY parser
            // exception lands here as 422 DocumentRejected, including one that
            // is a bug in parser code (a NullReferenceException in new
            // extraction logic) rather than the document's doing — where the
            // client side maps its own unclassified failures to Crashed so a
            // bug retries rather than retires. This matches the in-process
            // precedent, where any exception out of the parser was a document
            // fault, and the parser libraries throw too many exception types
            // for a "document fault" allowlist to be honest. The cost is that
            // a parser bug can retire documents; the remedy is `extract-
            // attachments --reextract-*` / `reocr` once it is fixed.
            budget.RequestCompleted(ctx);
            logger.LogWarning("parse: {Path} rejected the document: {Type}", ctx.Request.Path, ex.GetType().Name);
            return Error(StatusCodes.Status422UnprocessableEntity, ParseErrorTypes.DocumentRejected,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static IResult Error(int status, string type, string message, string? describe = null, long? limitBytes = null) =>
        Results.Json(new ParseError(type, message, describe, limitBytes), ParserWire.Json, statusCode: status);
}
