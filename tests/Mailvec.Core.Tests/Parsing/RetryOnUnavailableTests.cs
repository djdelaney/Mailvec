using Mailvec.Core.Attachments;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Parsing.Contracts;
using Mailvec.Pdf;

namespace Mailvec.Core.Tests.Parsing;

/// <summary>
/// The CLI backfills' answer to the parse host's routine recycle: wait for
/// the probe, retry, give up only if it stays down. Sleeps are recorded, not
/// slept.
/// </summary>
public class RetryOnUnavailableTests
{
    private static ParseException Unavailable() => new(ParseFailureKind.Unavailable, "connection refused");

    /// <summary>A parser whose DescribePart answers from a script and whose probe answers from another.</summary>
    private sealed class ScriptedParser(IEnumerable<Func<PartInfo>> calls, IEnumerable<bool> probes) : IMailParser
    {
        private readonly Queue<Func<PartInfo>> _calls = new(calls);
        private readonly Queue<bool> _probes = new(probes);
        public int Calls { get; private set; }
        public int Probes { get; private set; }

        public string Mode => "scripted";
        public PartInfo DescribePart(byte[] eml, int partIndex) { Calls++; return _calls.Dequeue()(); }
        public Task<bool> ProbeAsync(CancellationToken ct = default) { Probes++; return Task.FromResult(_probes.Count > 0 ? _probes.Dequeue() : false); }

        public ParsedMessage ParseMessage(byte[] eml, bool extractAttachmentText) => throw new NotSupportedException();
        public ExtractionResult ExtractAttachmentText(byte[] eml, int partIndex) => throw new NotSupportedException();
        public DecodedPart DecodePart(byte[] eml, int partIndex, long? maxBytes) => throw new NotSupportedException();
        public PdfRender RenderPdfPages(byte[] eml, int partIndex, int firstPage, int maxPages, long? maxBytes) => throw new NotSupportedException();
        public NormalizedImage? NormalizeImage(byte[] eml, int partIndex, long? maxBytes) => throw new NotSupportedException();
        public string? BodyTextFromHtml(string html, string? subject) => throw new NotSupportedException();
    }

    private static readonly PartInfo Ok = new("memo.txt", "text/plain");

    [Fact]
    public void Retries_once_the_probe_passes()
    {
        // The routine case: the host exited after its request budget and is
        // back by the first probe. One retry, no sleep, the caller never sees
        // the Unavailable.
        var inner = new ScriptedParser([() => throw Unavailable(), () => Ok], probes: [true]);
        var slept = new List<TimeSpan>();
        var parser = new RetryOnUnavailable(inner, TimeSpan.FromSeconds(60), sleep: slept.Add);

        parser.DescribePart([], 0).ShouldBe(Ok);

        inner.Calls.ShouldBe(2);
        inner.Probes.ShouldBe(1);
        slept.ShouldBeEmpty();
    }

    [Fact]
    public void Sleeps_between_failed_probes_and_gives_up_at_the_budget()
    {
        // A service that stays down: probe, nap, probe, nap … until the budget
        // is spent, then the ORIGINAL Unavailable is rethrown so the command
        // stops exactly as it would have without the wait.
        var inner = new ScriptedParser([() => throw Unavailable()], probes: []);
        var slept = new List<TimeSpan>();
        var parser = new RetryOnUnavailable(inner, TimeSpan.FromSeconds(5), sleep: slept.Add);

        var ex = Should.Throw<ParseException>(() => parser.DescribePart([], 0));

        ex.Kind.ShouldBe(ParseFailureKind.Unavailable);
        inner.Calls.ShouldBe(1, "no retry without a passing probe");
        slept.Sum(t => t.TotalSeconds).ShouldBe(5, 0.5, "naps are bounded by the remaining budget");
        slept.ShouldAllBe(t => t <= RetryOnUnavailable.ProbeInterval);
    }

    [Fact]
    public void A_recycle_during_the_retry_is_absorbed_within_the_budget()
    {
        // Probe passes, the retry hits the next recycle, probe passes again:
        // the retry succeeds within the same budget, with one nap between the
        // failed retry and the next probe so a flapping host cannot spin us.
        var inner = new ScriptedParser([() => throw Unavailable(), () => throw Unavailable(), () => Ok], probes: [true, true]);
        var slept = new List<TimeSpan>();
        var parser = new RetryOnUnavailable(inner, TimeSpan.FromSeconds(60), sleep: slept.Add);

        parser.DescribePart([], 0).ShouldBe(Ok);

        inner.Calls.ShouldBe(3);
        slept.ShouldHaveSingleItem().ShouldBe(RetryOnUnavailable.ProbeInterval);
    }

    [Fact]
    public void A_crash_is_never_retried()
    {
        // Crashed is a statement about the document (the host timed out on
        // it and exited): retrying re-crashes the host. It goes straight
        // through, probe untouched.
        var inner = new ScriptedParser([() => throw new ParseException(ParseFailureKind.Crashed, "504")], probes: [true]);
        var parser = new RetryOnUnavailable(inner, TimeSpan.FromSeconds(60), sleep: _ => { });

        Should.Throw<ParseException>(() => parser.DescribePart([], 0)).Kind.ShouldBe(ParseFailureKind.Crashed);
        inner.Probes.ShouldBe(0);
    }

    [Fact]
    public void Wrap_returns_the_inner_parser_when_the_wait_is_zero()
    {
        var inner = new ScriptedParser([], []);

        RetryOnUnavailable.Wrap(inner, new ParserOptions { UnavailableWaitSeconds = 0 }).ShouldBeSameAs(inner);
        RetryOnUnavailable.Wrap(inner, new ParserOptions { UnavailableWaitSeconds = 60 }).ShouldBeOfType<RetryOnUnavailable>();
    }

    [Fact]
    public void The_wait_is_reported_to_the_operator()
    {
        var inner = new ScriptedParser([() => throw Unavailable(), () => Ok], probes: [true]);
        var lines = new List<string>();
        var parser = new RetryOnUnavailable(inner, TimeSpan.FromSeconds(60), report: lines.Add, sleep: _ => { });

        parser.DescribePart([], 0);

        lines.Count.ShouldBe(2);
        lines[0].ShouldContain("waiting up to 60s");
        lines[1].ShouldContain("back after");
    }
}
