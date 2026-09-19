using Mailvec.Core.Attachments;
using Mailvec.Core.Options;
using Mailvec.Parsing.Contracts;
using Mailvec.Pdf;

namespace Mailvec.Core.Parsing;

/// <summary>
/// An <see cref="IMailParser"/> decorator for the CLI backfills: when a call
/// fails with <see cref="ParseFailureKind.Unavailable"/>, wait for the parse
/// service to answer its probe again and retry the call, giving up — and
/// rethrowing the original <c>Unavailable</c> — only if it stays down for
/// <see cref="ParserOptions.UnavailableWaitSeconds"/>.
///
/// <para><b>Why a wait, not a stop.</b> The parse host exits on purpose after
/// <c>MaxRequestsBeforeExit</c> requests (500 by default) and is back in
/// seconds. Stopping on the first <c>Unavailable</c> — what the backfills did
/// after phase 3 — turned every long run into a rerun every ~500 messages,
/// and <c>rebuild-bodies</c>, which re-selects every row each run, could
/// never finish a large archive at all. A bounded wait rides out the
/// routine recycle; a service that is genuinely down still stops the run.</para>
///
/// <para><b>Why the probe.</b> The retried call re-sends the whole
/// <c>.eml</c>; <c>GET /up</c> is what to spend while waiting. This is the
/// same evidence the OCR pass uses to settle parser strikes, and it is only
/// ever consulted <em>after</em> a failure — never as a gate in front of a
/// call (CLAUDE.md: a probe per call couples availability).</para>
///
/// <para>Only <c>Unavailable</c> is retried. <c>Crashed</c> is a statement
/// about the document and goes straight through, and so does every other
/// exception. The unattended services (scanner, OCR pass) do not use this:
/// their next tick is the retry.</para>
/// </summary>
public sealed class RetryOnUnavailable : IMailParser
{
    /// <summary>How often the service is probed while waiting.</summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);

    private readonly IMailParser _inner;
    private readonly TimeSpan _budget;
    private readonly Action<TimeSpan> _sleep;
    private readonly Action<string>? _report;

    /// <param name="sleep">Test seam; defaults to <see cref="Thread.Sleep(TimeSpan)"/>.</param>
    public RetryOnUnavailable(IMailParser inner, TimeSpan budget, Action<string>? report = null, Action<TimeSpan>? sleep = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _budget = budget < TimeSpan.Zero ? TimeSpan.Zero : budget;
        _report = report;
        _sleep = sleep ?? Thread.Sleep;
    }

    /// <summary>
    /// The parser the CLI commands should use: the inner one unchanged when
    /// the configured wait is zero (so behaviour is exactly the pre-retry
    /// stop), otherwise wrapped.
    /// </summary>
    public static IMailParser Wrap(IMailParser inner, ParserOptions options, TextWriter? report = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.UnavailableWaitSeconds <= 0) return inner;
        return new RetryOnUnavailable(inner, TimeSpan.FromSeconds(options.UnavailableWaitSeconds),
            report is null ? null : line => report.WriteLine(line));
    }

    public string Mode => _inner.Mode;

    public ParsedMessage ParseMessage(byte[] eml, bool extractAttachmentText) =>
        Run(() => _inner.ParseMessage(eml, extractAttachmentText));
    public ExtractionResult ExtractAttachmentText(byte[] eml, int partIndex) =>
        Run(() => _inner.ExtractAttachmentText(eml, partIndex));
    public PartInfo DescribePart(byte[] eml, int partIndex) =>
        Run(() => _inner.DescribePart(eml, partIndex));
    public DecodedPart DecodePart(byte[] eml, int partIndex, long? maxBytes) =>
        Run(() => _inner.DecodePart(eml, partIndex, maxBytes));
    public PdfRender RenderPdfPages(byte[] eml, int partIndex, int firstPage, int maxPages, long? maxBytes) =>
        Run(() => _inner.RenderPdfPages(eml, partIndex, firstPage, maxPages, maxBytes));
    public NormalizedImage? NormalizeImage(byte[] eml, int partIndex, long? maxBytes) =>
        Run(() => _inner.NormalizeImage(eml, partIndex, maxBytes));
    public string? BodyTextFromHtml(string html, string? subject) =>
        Run(() => _inner.BodyTextFromHtml(html, subject));
    public Task<bool> ProbeAsync(CancellationToken ct = default) => _inner.ProbeAsync(ct);

    // The budget is spent in naps, not measured on the wall clock: the sum of
    // what we asked the sleep seam for is what counts. That makes the wait
    // deterministic under an injected sleep, and it is honest either way —
    // time spent inside the probe or the retried call is the service's, not
    // the wait's.
    private T Run<T>(Func<T> call)
    {
        var waiting = false;
        var waited = TimeSpan.Zero;
        while (true)
        {
            try
            {
                return call();
            }
            catch (ParseException ex) when (ex.Kind == ParseFailureKind.Unavailable)
            {
                if (!waiting)
                {
                    waiting = true;
                    _report?.Invoke($"  parse service unavailable ({ex.Message}); waiting up to {_budget.TotalSeconds:N0}s for it to return.");
                }
                else
                {
                    // A retry failed after a passing probe (the host answered
                    // /up and then recycled again, or answers /up but not
                    // parses). Nap before probing again so this cannot spin.
                    if (!Nap(ref waited)) throw;
                }
                if (!WaitUntilUp(ref waited)) throw;
                _report?.Invoke($"  parse service is back after {waited.TotalSeconds:N0}s; retrying.");
            }
        }
    }

    /// <summary>Sleep one probe interval, bounded by what is left of the budget. False = the budget is spent.</summary>
    private bool Nap(ref TimeSpan waited)
    {
        var remaining = _budget - waited;
        if (remaining <= TimeSpan.Zero) return false;
        var nap = remaining < ProbeInterval ? remaining : ProbeInterval;
        _sleep(nap);
        waited += nap;
        return true;
    }

    /// <summary>Probe until the service answers or the budget is spent. False = give up.</summary>
    private bool WaitUntilUp(ref TimeSpan waited)
    {
        while (true)
        {
            if (_inner.ProbeAsync().GetAwaiter().GetResult()) return true;
            if (!Nap(ref waited)) return false;
        }
    }
}
