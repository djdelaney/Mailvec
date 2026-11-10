using Mailvec.Core.Attachments;
using Mailvec.Core.Parsing;
using Mailvec.Parsing.Contracts;
using Mailvec.Pdf;

namespace Mailvec.Core.Tests.Parsing;

/// <summary>
/// In-process there is no parse service to time a call out, so a document that
/// keeps a parser busy forever used to hold its caller forever: the embedder's
/// OCR pass (and with it all embedding) or the indexer's scan. These pin what
/// an overrun turns into, because each kind sends the callers down a different
/// path and the wrong one either wedges again or retires a healthy document.
/// </summary>
public class TimeBoundedParserTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(150);

    /// <summary>A parser whose calls block until released, so a test decides when (or whether) they return.</summary>
    private sealed class GatedParser : IMailParser, IDisposable
    {
        public ManualResetEventSlim Gate { get; } = new(initialState: true);
        public int RenderCalls;
        public Exception? Throw { get; set; }

        private T Wait<T>(T value)
        {
            Gate.Wait();
            if (Throw is not null) throw Throw;
            return value;
        }

        public string Mode => "gated";
        public ParsedMessage ParseMessage(byte[] eml, bool extractAttachmentText) => Wait<ParsedMessage>(null!);
        public ExtractionResult ExtractAttachmentText(byte[] eml, int partIndex) => Wait(new ExtractionResult("text", "done"));
        public PartInfo DescribePart(byte[] eml, int partIndex) => Wait(new PartInfo("a.pdf", "application/pdf"));
        public DecodedPart DecodePart(byte[] eml, int partIndex, long? maxBytes) => Wait(new DecodedPart("a.pdf", "application/pdf", [1]));
        public NormalizedImage? NormalizeImage(byte[] eml, int partIndex, long? maxBytes) => Wait<NormalizedImage?>(null);
        public string? BodyTextFromHtml(string html, string? subject) => Wait<string?>("body");

        public PdfRender RenderPdfPages(byte[] eml, int partIndex, int firstPage, int maxPages, long? maxBytes)
        {
            Interlocked.Increment(ref RenderCalls);
            return Wait(new PdfRender(1, [new byte[] { 1 }]));
        }

        public void Dispose() => Gate.Set(); // never leave a pool thread parked after the test
    }

    [Fact]
    public void A_call_that_returns_in_time_passes_its_result_through()
    {
        using var inner = new GatedParser();
        var parser = new TimeBoundedParser(inner, TimeSpan.FromSeconds(30));

        parser.ExtractAttachmentText([], 0).Text.ShouldBe("text");
        parser.RenderPdfPages([], 0, 0, 1, null).PageCount.ShouldBe(1);
        parser.Mode.ShouldBe("gated");
    }

    [Fact]
    public void The_parsers_own_exception_reaches_the_caller_unwrapped()
    {
        // Callers branch on these types (the MCP tools translate
        // AttachmentTooLargeException, ArgumentOutOfRangeException means "no
        // such part"). An AggregateException here would read as a different
        // failure and, in the OCR pass, retire the document.
        using var inner = new GatedParser { Throw = new AttachmentTooLargeException("'big.pdf'", 1024) };
        var parser = new TimeBoundedParser(inner, TimeSpan.FromSeconds(30));

        Should.Throw<AttachmentTooLargeException>(() => parser.DecodePart([], 0, 1024));
        Should.Throw<AttachmentTooLargeException>(() => parser.RenderPdfPages([], 0, 0, 1, 1024));
    }

    [Fact]
    public void A_parse_that_overruns_is_a_strike_not_a_verdict()
    {
        using var inner = new GatedParser();
        inner.Gate.Reset();
        var parser = new TimeBoundedParser(inner, Short);

        Should.Throw<ParseException>(() => parser.ParseMessage([], extractAttachmentText: true))
            .Kind.ShouldBe(ParseFailureKind.Crashed);
        Should.Throw<ParseException>(() => parser.ExtractAttachmentText([], 0))
            .Kind.ShouldBe(ParseFailureKind.Crashed);
    }

    [Fact]
    public void A_render_that_overruns_is_a_verdict_on_that_document()
    {
        // Not a strike: strikes are in memory and the stuck render outlives
        // them, so the same PDF would be selected again after every restart.
        using var inner = new GatedParser();
        inner.Gate.Reset();
        var parser = new TimeBoundedParser(inner, Short);

        Should.Throw<ParseException>(() => parser.RenderPdfPages([], 0, 0, 1, null))
            .Kind.ShouldBe(ParseFailureKind.DocumentRejected);
    }

    [Fact]
    public void While_a_render_is_stuck_other_renders_are_refused_without_being_started()
    {
        // PDFium is not re-entrant: a second render can queue behind the stuck
        // one, overrun through no fault of its own and be retired. Unavailable
        // counts nothing against the document.
        using var inner = new GatedParser();
        inner.Gate.Reset();
        var parser = new TimeBoundedParser(inner, Short);
        Should.Throw<ParseException>(() => parser.RenderPdfPages([], 0, 0, 1, null));

        Should.Throw<ParseException>(() => parser.RenderPdfPages([], 1, 0, 1, null))
            .Kind.ShouldBe(ParseFailureKind.Unavailable);

        inner.RenderCalls.ShouldBe(1);
    }

    [Fact]
    public void A_stuck_render_does_not_stop_the_rest_of_the_parser()
    {
        // The scanner abandons its whole walk on Unavailable, so a stuck render
        // in one process must never surface there. Parsing shares nothing with
        // PDFium and keeps working.
        using var inner = new GatedParser();
        inner.Gate.Reset();
        var parser = new TimeBoundedParser(inner, Short);
        Should.Throw<ParseException>(() => parser.RenderPdfPages([], 0, 0, 1, null));

        Should.Throw<ParseException>(() => parser.ExtractAttachmentText([], 0))
            .Kind.ShouldBe(ParseFailureKind.Crashed); // still its own timeout, never Unavailable
    }

    [Fact]
    public void Rendering_resumes_once_the_stuck_render_returns()
    {
        using var inner = new GatedParser();
        inner.Gate.Reset();
        var parser = new TimeBoundedParser(inner, Short);
        Should.Throw<ParseException>(() => parser.RenderPdfPages([], 0, 0, 1, null));

        inner.Gate.Set();

        // The release is observed on a continuation, so give it a moment.
        PdfRender? rendered = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (rendered is null && DateTime.UtcNow < deadline)
        {
            try { rendered = parser.RenderPdfPages([], 0, 0, 1, null); }
            catch (ParseException ex) when (ex.Kind == ParseFailureKind.Unavailable) { Thread.Sleep(20); }
        }
        rendered.ShouldNotBeNull().PageCount.ShouldBe(1);
    }
}
