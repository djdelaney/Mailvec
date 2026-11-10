using System.Runtime.ExceptionServices;
using Mailvec.Core.Attachments;
using Mailvec.Parsing.Contracts;
using Mailvec.Pdf;

namespace Mailvec.Core.Parsing;

/// <summary>
/// Puts a wall-clock ceiling on every call into an in-process
/// <see cref="IMailParser"/>, and reports a call that overran through the same
/// <see cref="ParseException"/> kinds the remote parser uses.
/// </summary>
/// <remarks>
/// In the container a parse that never returns is the parse service's problem:
/// it answers 504, exits, and every caller already knows what to do with the
/// resulting <see cref="ParseFailureKind.Crashed"/>. In-process there was
/// nothing equivalent. The parsers take no cancellation token, so a document
/// that keeps PDFium rendering (a 958-byte shading PDF ran past 180 s) or keeps
/// PdfPig walking pages held its caller for good: the embedder's OCR pass never
/// came back, so nothing was embedded either, and the indexer's scan stopped
/// at that file. Anyone who can send the user mail could cause it.
///
/// A managed thread cannot be killed, so an overrun call is ABANDONED, not
/// cancelled: it keeps its thread until the parser returns, and its result is
/// thrown away. That is the whole cost, and it is why the kinds differ:
///
/// <list type="bullet">
/// <item>Everything except rendering overruns as
/// <see cref="ParseFailureKind.Crashed"/> — a strike, so the scanner's
/// per-file crash count and the OCR pass's sweeps decide when to give up,
/// exactly as they do for the remote parser.</item>
/// <item><see cref="RenderPdfPages"/> overruns as
/// <see cref="ParseFailureKind.DocumentRejected"/>, a verdict. Strikes live in
/// memory and the stuck render outlives them: counted as a strike, the document
/// would be picked again after every restart and wedge OCR each time.</item>
/// <item>While an abandoned render is still running, further renders are
/// refused as <see cref="ParseFailureKind.Unavailable"/> without being started.
/// PDFium is not re-entrant, so a second render may simply queue behind the
/// stuck one; letting it run would time out an innocent document and retire it.
/// Unavailable counts nothing, and clears by itself if the render ever
/// finishes. If it never does, PDF rendering stays off until the process
/// restarts — by which point the document that caused it is already
/// <c>failed</c> and will not be selected again.</item>
/// </list>
///
/// Only <see cref="ParserRegistration"/> applies this, and only to the
/// in-process parser. The remote client has its own timeout and classification.
/// </remarks>
public sealed class TimeBoundedParser(IMailParser inner, TimeSpan timeout) : IMailParser
{
    private int _stuckRenders;

    /// <summary>The parser being bounded. Exposed for registration tests.</summary>
    public IMailParser Inner { get; } = inner;

    public string Mode => Inner.Mode;

    public ParsedMessage ParseMessage(byte[] eml, bool extractAttachmentText) =>
        Bounded("parsing a message", () => Inner.ParseMessage(eml, extractAttachmentText));

    public ExtractionResult ExtractAttachmentText(byte[] eml, int partIndex) =>
        Bounded("extracting attachment text", () => Inner.ExtractAttachmentText(eml, partIndex));

    public PartInfo DescribePart(byte[] eml, int partIndex) =>
        Bounded("describing an attachment", () => Inner.DescribePart(eml, partIndex));

    public DecodedPart DecodePart(byte[] eml, int partIndex, long? maxBytes) =>
        Bounded("decoding an attachment", () => Inner.DecodePart(eml, partIndex, maxBytes));

    public NormalizedImage? NormalizeImage(byte[] eml, int partIndex, long? maxBytes) =>
        Bounded("decoding an image", () => Inner.NormalizeImage(eml, partIndex, maxBytes));

    public string? BodyTextFromHtml(string html, string? subject) =>
        Bounded("converting an HTML body", () => Inner.BodyTextFromHtml(html, subject));

    public Task<bool> ProbeAsync(CancellationToken ct = default) => Inner.ProbeAsync(ct);

    public PdfRender RenderPdfPages(byte[] eml, int partIndex, int firstPage, int maxPages, long? maxBytes)
    {
        if (Volatile.Read(ref _stuckRenders) > 0)
            throw new ParseException(
                ParseFailureKind.Unavailable,
                "PDF rendering is paused: an earlier render in this process overran its time limit and has not returned. " +
                "It resumes when that render finishes or the process restarts.");

        var work = Task.Run(() => Inner.RenderPdfPages(eml, partIndex, firstPage, maxPages, maxBytes));
        if (Finished(work)) return Unwrap(work);

        Interlocked.Increment(ref _stuckRenders);
        work.ContinueWith(
            t => { _ = t.Exception; Interlocked.Decrement(ref _stuckRenders); },
            TaskScheduler.Default);
        throw new ParseException(
            ParseFailureKind.DocumentRejected,
            $"Rendering this PDF did not finish within {timeout.TotalSeconds:0} seconds and was abandoned.");
    }

    private T Bounded<T>(string what, Func<T> call)
    {
        var work = Task.Run(call);
        if (Finished(work)) return Unwrap(work);

        // Observe the eventual fault so an abandoned call cannot surface later
        // as an unobserved task exception.
        work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        throw new ParseException(
            ParseFailureKind.Crashed,
            $"The in-process parser did not finish {what} within {timeout.TotalSeconds:0} seconds and was abandoned.");
    }

    private bool Finished(Task work)
    {
        try
        {
            return work.Wait(timeout);
        }
        catch (AggregateException)
        {
            return true; // faulted inside the window: Unwrap rethrows the real exception
        }
    }

    /// <summary>
    /// The call's own result or its own exception, with the original type and
    /// stack: callers branch on what the in-process parser throws
    /// (<c>ArgumentOutOfRangeException</c>, <c>AttachmentTooLargeException</c>),
    /// and an <see cref="AggregateException"/> would read as a different failure.
    /// </summary>
    private static T Unwrap<T>(Task<T> work)
    {
        if (work.Exception?.InnerException is { } failure)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return work.Result;
    }
}
