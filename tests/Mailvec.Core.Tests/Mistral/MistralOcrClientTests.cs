using System.Net;
using System.Text;
using System.Text.Json;
using Mailvec.Core.Mistral;
using Mailvec.Core.Options;
using Mailvec.Core.Vision;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;
using Shouldly;

namespace Mailvec.Core.Tests.Mistral;

public class MistralOcrClientTests
{
    // ---- Placeholder stripping ------------------------------------------------
    //
    // The highest-stakes behaviour in this class. mistral-ocr takes no prompt, so
    // unlike the Ollama path there is no "reply NO_TEXT_FOUND" sentinel to lean
    // on — a textless picture comes back as image placeholders and nothing else.
    // If those reach the caller as text, the attachment is written back as
    // status='ocr' and a photograph becomes searchable by the literal string
    // "![img-0.jpeg](img-0.jpeg)", while MarkAttachmentImageNoText never fires.
    // Silent index corruption, permanent, with nothing left to re-trigger.

    [Fact]
    public void StripPlaceholders_reduces_a_placeholder_only_response_to_empty()
    {
        MistralOcrClient.StripPlaceholders("![img-0.jpeg](img-0.jpeg)").ShouldBe("");
        MistralOcrClient.StripPlaceholders(
            "![img-0.jpeg](img-0.jpeg)\n\n![img-1.jpeg](img-1.jpeg)\n\n![img-2.jpeg](img-2.jpeg)")
            .ShouldBe("");
    }

    [Fact]
    public void StripPlaceholders_keeps_real_text_beside_a_placeholder()
    {
        // The common case on a scanned page: a photo region plus actual text.
        // Dropping the placeholder must not drop the text with it.
        var result = MistralOcrClient.StripPlaceholders(
            "![img-0.jpeg](img-0.jpeg)\n\nACME BANK\n\nStatement of Account\n\n![img-1.png](img-1.png)");
        result.ShouldBe("ACME BANK\nStatement of Account");
    }

    [Fact]
    public void StripPlaceholders_preserves_table_and_heading_structure()
    {
        // Structure carries meaning for search; only blank lines left behind by
        // a removal get collapsed.
        var result = MistralOcrClient.StripPlaceholders("# Invoice\n| Item | Qty |\n| Widget | 2 |");
        result.ShouldBe("# Invoice\n| Item | Qty |\n| Widget | 2 |");
    }

    [Fact]
    public async Task A_textless_image_yields_empty_so_the_caller_can_mark_it_no_text()
    {
        // End to end through the client, since this is the path that decides
        // whether an image is indexed as garbage or correctly recorded as
        // having no text.
        var client = ClientWith(_ => OkPages("![img-0.jpeg](img-0.jpeg)"));
        (await client.OcrImageAsync([1])).ShouldBe(string.Empty);
    }

    // ---- Output cap -----------------------------------------------------------

    [Fact]
    public async Task Runaway_output_is_capped()
    {
        // Stands in for Ollama's num_predict, which has no hosted equivalent.
        // Observed for real: mistral-ocr repetition-looped on a dense
        // architectural drawing. Note the loop was *within* one pipe-table line,
        // so a line-level collapse would not have caught it — hence a hard cap.
        var runaway = string.Join(" • ", Enumerable.Repeat("ARCHITECTURAL ALLIANCE SHEET", 5000));
        var client = ClientWith(_ => OkPages(runaway), o => o.MaxCharsPerCall = 1000);

        var text = await client.OcrAsync([1]);
        text.Length.ShouldBe(1000);
    }

    [Fact]
    public async Task Output_under_the_cap_is_untouched()
    {
        var client = ClientWith(_ => OkPages("Short and legitimate."), o => o.MaxCharsPerCall = 1000);
        (await client.OcrAsync([1])).ShouldBe("Short and legitimate.");
    }

    // ---- Failure classification ----------------------------------------------
    //
    // Each of these decides whether a perfectly good document survives. See
    // VisionFailureKind for what a misclassification costs.

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, VisionFailureKind.Backpressure)]
    [InlineData(HttpStatusCode.Unauthorized, VisionFailureKind.AuthOrConfig)]
    [InlineData(HttpStatusCode.Forbidden, VisionFailureKind.AuthOrConfig)]
    [InlineData(HttpStatusCode.NotFound, VisionFailureKind.AuthOrConfig)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, VisionFailureKind.DocumentFatal)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, VisionFailureKind.DocumentFatal)]
    [InlineData(HttpStatusCode.UnprocessableEntity, VisionFailureKind.DocumentFatal)]
    [InlineData(HttpStatusCode.BadGateway, VisionFailureKind.Transient)]
    public async Task Http_status_maps_to_the_right_failure_kind(HttpStatusCode status, VisionFailureKind expected)
    {
        // MaxRetries 0 so retryable statuses surface immediately rather than
        // making the test wait out a backoff.
        var client = ClientWith(_ => new HttpResponseMessage(status) { Content = new StringContent("nope") },
            o => o.MaxRetries = 0);

        var ex = await Should.ThrowAsync<VisionException>(() => client.OcrAsync([1]));
        ex.Kind.ShouldBe(expected);
    }

    [Fact]
    public async Task The_response_body_stays_out_of_the_exception_message()
    {
        // The request was a rendered page of the user's mail; a service that
        // echoes it back must not have that land in a log line. Same rule as
        // OllamaVisionClient — body goes on Data, not into the message.
        var client = ClientWith(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("SENSITIVE ACCOUNT 123456"),
        }, o => o.MaxRetries = 0);

        var ex = await Should.ThrowAsync<VisionException>(() => client.OcrAsync([1]));
        ex.Message.ShouldNotContain("SENSITIVE");
        ex.Data["body"]!.ToString()!.ShouldContain("SENSITIVE"); // still available for diagnosis
    }

    [Fact]
    public async Task A_rate_limit_is_retried_before_it_is_surfaced_as_backpressure()
    {
        // Short bursts should be absorbed in-client; only a persistent limit
        // becomes Backpressure for the OCR pass to act on.
        var calls = 0;
        var client = ClientWith(_ =>
        {
            calls++;
            return calls <= 2
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) },
                }
                : OkPages("RECOVERED");
        }, o => { o.MaxRetries = 3; o.MinIntervalMs = 0; });

        (await client.OcrAsync([1])).ShouldBe("RECOVERED");
        calls.ShouldBe(3);
    }

    [Fact]
    public async Task Pages_are_ordered_by_the_services_own_index()
    {
        // Never trust array order: page N of the output must line up with page N
        // of the request or the text is silently scrambled.
        var client = ClientWith(_ => Ok(new
        {
            pages = new[]
            {
                new { index = 2, markdown = "THIRD" },
                new { index = 0, markdown = "FIRST" },
                new { index = 1, markdown = "SECOND" },
            },
        }));

        (await client.OcrAsync([1])).ShouldBe("FIRST\nSECOND\nTHIRD");
    }

    // ---- Availability probe ---------------------------------------------------

    [Fact]
    public async Task Probe_treats_422_as_available()
    {
        // The probe deliberately posts a body with no `document`, so a live
        // route with good credentials answers 422. That proves reachability and
        // auth without submitting mail content or incurring a billable page —
        // which matters because it runs every OCR cycle.
        var client = ClientWith(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));
        (await client.IsModelAvailableAsync()).ShouldBeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Probe_reports_unavailable_for_bad_credentials_or_route(HttpStatusCode status)
    {
        var client = ClientWith(_ => new HttpResponseMessage(status));
        (await client.IsModelAvailableAsync()).ShouldBeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, VisionProbeStatus.AuthFailed)]
    [InlineData(HttpStatusCode.Forbidden, VisionProbeStatus.AuthFailed)]
    [InlineData(HttpStatusCode.NotFound, VisionProbeStatus.RouteNotFound)]
    [InlineData(HttpStatusCode.BadGateway, VisionProbeStatus.Unreachable)]
    public async Task Probe_says_WHY_it_failed(HttpStatusCode status, VisionProbeStatus expected)
    {
        // A bare bool collapses "the key is wrong" and "the deployment name is
        // wrong" into one indistinguishable false, and those send the operator
        // to completely different settings.
        var client = ClientWith(_ => new HttpResponseMessage(status));
        (await client.ProbeAsync()).Status.ShouldBe(expected);
    }

    [Fact]
    public async Task Probe_reports_available_on_422()
    {
        (await ClientWith(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)).ProbeAsync())
            .Status.ShouldBe(VisionProbeStatus.Available);
    }

    [Fact]
    public async Task Probe_reports_unavailable_when_the_endpoint_is_unreachable()
    {
        var client = ClientWith(_ => throw new HttpRequestException("no route to host"));
        (await client.IsModelAvailableAsync()).ShouldBeFalse();
    }

    // ---- helpers --------------------------------------------------------------

    private static MistralOcrClient ClientWith(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        Action<MistralVisionOptions>? configure = null)
    {
        var opts = new VisionOptions
        {
            Provider = VisionOptions.ProviderMistral,
            Mistral = new MistralVisionOptions
            {
                Endpoint = "https://example.invalid",
                Model = "mistral-ocr-test",
                ApiKey = "k",
                MinIntervalMs = 0,
            },
        };
        configure?.Invoke(opts.Mistral);

        var http = new HttpClient(new StubHandler(handler))
        {
            BaseAddress = new Uri("https://example.invalid/"),
        };
        return new MistralOcrClient(http, MsOptions.Create(opts), NullLogger<MistralOcrClient>.Instance);
    }

    private static HttpResponseMessage OkPages(params string[] markdown) =>
        Ok(new { pages = markdown.Select((m, i) => new { index = i, markdown = m }).ToArray() });

    private static HttpResponseMessage Ok(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handler(request));
    }
}
