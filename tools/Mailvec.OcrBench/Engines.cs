using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Mailvec.Core.Vision;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailvec.OcrBench;

/// <summary>
/// An OCR engine under test. Two shapes, because the contenders don't agree on
/// one: <see cref="OcrPageAsync"/> takes a rendered page image (what
/// AttachmentOcrService does today), <see cref="OcrDocumentAsync"/> takes a
/// whole PDF and returns per-page text (what mistral-ocr is actually built for).
/// An engine implements whichever it supports; the runner picks by mode.
/// </summary>
internal interface IOcrEngine : IDisposable
{
    string Name { get; }
    string Detail { get; }
    bool SupportsPageMode { get; }
    bool SupportsDocumentMode { get; }

    /// <summary>
    /// Milliseconds the most recent call spent NOT waiting on the service —
    /// client-side pacing and retry backoff. The runner subtracts it so the
    /// recorded latency is service response time rather than a measure of how
    /// politely the harness is throttling itself. Always 0 for a local engine.
    /// The run's wall clock still reflects the throttled reality.
    /// </summary>
    long LastCallWaitMs { get; }

    Task<string> OcrPageAsync(byte[] jpeg, CancellationToken ct);

    /// <summary>Per-page text for the whole PDF, indexed 0..n-1 in page order.</summary>
    Task<IReadOnlyList<string>> OcrDocumentAsync(byte[] pdf, CancellationToken ct);
}

/// <summary>
/// The incumbent: the production <see cref="OllamaVisionClient"/>, unmodified
/// and reading the same <c>Ollama:*</c> config the embedder uses — so this
/// measures what actually runs today, mitigations and all
/// (VisionMaxTokens / num_predict, CollapseRepeatedLines, the document prompt).
///
/// Deliberately NOT tuned for the bake-off. If it loses, the follow-up question
/// is whether relaxing those knobs closes the gap; answering that means running
/// this engine again with them changed, not quietly changing them here.
/// </summary>
internal sealed class OllamaEngine : IOcrEngine, IDisposable
{
    private readonly HttpClient _http;
    private readonly OllamaVisionClient _client;
    private readonly OllamaOptions _opts;

    public OllamaEngine(OllamaOptions opts)
    {
        _opts = opts;
        _http = new HttpClient
        {
            BaseAddress = new Uri(opts.BaseUrl),
            Timeout = TimeSpan.FromSeconds(Math.Max(30, opts.VisionRequestTimeoutSeconds)),
        };
        _client = new OllamaVisionClient(_http, Options.Create(opts), NullLogger<OllamaVisionClient>.Instance);
    }

    public string Name => "ollama";
    public string Detail => $"{_opts.VisionModel} @ {_opts.BaseUrl} (num_predict={_opts.VisionMaxTokens})";
    public bool SupportsPageMode => true;
    public bool SupportsDocumentMode => false;

    /// <summary>Nothing is paced locally — the model serialises on the GPU anyway.</summary>
    public long LastCallWaitMs => 0;

    public Task<string> OcrPageAsync(byte[] jpeg, CancellationToken ct) => _client.OcrAsync(jpeg, ct);

    public Task<IReadOnlyList<string>> OcrDocumentAsync(byte[] pdf, CancellationToken ct) =>
        throw new NotSupportedException("Ollama vision has no document mode — it takes one image per call.");

    public Task<bool> IsAvailableAsync(CancellationToken ct) => _client.IsModelAvailableAsync(ct);

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// mistral-ocr, over the OCR REST API — the same request shape whether it's
/// hosted on Azure AI Foundry or api.mistral.ai; only the base URL, the route
/// and the auth header differ, all of which are flags.
///
/// Both modes are exercised because they are genuinely different products:
///   page     — one call per rendered JPEG (type "image_url"). Apples-to-apples
///              with the Ollama engine: identical pixels in, text out.
///   document — one call for the whole PDF (type "document_url"), which is what
///              the model is designed for. Its own PDF handling replaces
///              PDFtoImage/PDFium, so this measures the engine AND its
///              rasterisation together — better numbers are expected, and they
///              are not attributable to the model alone.
///
/// Both take a base64 data URI, so nothing is uploaded to blob storage and no
/// document is ever exposed at a fetchable URL.
/// </summary>
internal sealed class MistralOcrEngine : IOcrEngine, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _route;

    private readonly int _minIntervalMs;
    private readonly int _maxRetries;
    private DateTimeOffset _nextCallAt = DateTimeOffset.MinValue;

    public MistralOcrEngine(
        string endpoint, string route, string model, string apiKey, string authHeader,
        int timeoutSeconds, int minIntervalMs, int maxRetries)
    {
        _model = model;
        _route = route;
        _minIntervalMs = minIntervalMs;
        _maxRetries = maxRetries;
        _http = new HttpClient
        {
            BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };

        // Azure AI Foundry serverless deployments accept `Authorization: Bearer
        // <key>`; some Azure OpenAI-style routes want `api-key: <key>` instead.
        // Both are one flag apart rather than a code change.
        if (authHeader.Equals("bearer", StringComparison.OrdinalIgnoreCase))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        else
            _http.DefaultRequestHeaders.Add(authHeader, apiKey);
    }

    public string Name => "mistral-ocr";
    public string Detail => $"{_model} @ {_http.BaseAddress}{_route}";
    public bool SupportsPageMode => true;
    public bool SupportsDocumentMode => true;

    public long LastCallWaitMs { get; private set; }

    public async Task<string> OcrPageAsync(byte[] jpeg, CancellationToken ct)
    {
        var pages = await PostAsync(new OcrRequest
        {
            Model = _model,
            Document = new DocumentRef { Type = "image_url", ImageUrl = DataUri("image/jpeg", jpeg) },
        }, ct).ConfigureAwait(false);

        // A single image is one page; join defensively in case the service
        // decides otherwise rather than silently dropping content.
        return string.Join("\n\n", pages);
    }

    public Task<IReadOnlyList<string>> OcrDocumentAsync(byte[] pdf, CancellationToken ct) =>
        PostAsync(new OcrRequest
        {
            Model = _model,
            Document = new DocumentRef { Type = "document_url", DocumentUrl = DataUri("application/pdf", pdf) },
        }, ct);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string DataUri(string mime, byte[] bytes) =>
        $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

    /// <summary>
    /// POST with pacing and 429/5xx retry.
    ///
    /// Throttling is a property of the DEPLOYMENT'S QUOTA, not of the model, so
    /// letting 429s stand would benchmark the Azure subscription rather than the
    /// OCR engine. Worse, it would do so invisibly in the direction of a win: a
    /// rejected call returns in milliseconds, so an unpaced run reports a
    /// spectacular mean latency that is mostly the service saying no. (Observed:
    /// 60 of 71 pages rejected at a headline "0.9 s/page".) Pace first, retry as
    /// the safety net, and let the scorer report whatever still failed.
    /// </summary>
    private async Task<IReadOnlyList<string>> PostAsync(OcrRequest request, CancellationToken ct)
    {
        // Serialise to a buffered StringContent rather than PostAsJsonAsync.
        // The latter streams, which sends Transfer-Encoding: chunked with no
        // Content-Length — and the Azure AI Foundry gateway rejects that
        // outright ("no_content_length_header", 400) before the model sees the
        // request. Buffering costs one extra copy of the base64 payload and is
        // the only thing that makes this route work.
        var json = JsonSerializer.Serialize(request, SerializerOptions);

        LastCallWaitMs = 0;
        HttpResponseMessage? response = null;
        for (var attempt = 0; ; attempt++)
        {
            var wait = _nextCallAt - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                LastCallWaitMs += (long)wait.TotalMilliseconds;
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            // A fresh StringContent per attempt: HttpContent is single-use and
            // reusing it across a retry throws rather than resending.
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            response?.Dispose();
            response = await _http.PostAsync(_route, content, ct).ConfigureAwait(false);
            _nextCallAt = DateTimeOffset.UtcNow.AddMilliseconds(_minIntervalMs);

            var retryable = (int)response.StatusCode == 429 || (int)response.StatusCode >= 500;
            if (!retryable || attempt >= _maxRetries) break;

            // Prefer the service's own Retry-After; it knows its quota window.
            // Otherwise exponential backoff from 2s, capped so one hot document
            // can't stall a run for minutes.
            var retryAfter = response.Headers.RetryAfter?.Delta
                ?? TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, attempt)));
            LastCallWaitMs += (long)retryAfter.TotalMilliseconds;
            await Task.Delay(retryAfter, ct).ConfigureAwait(false);
        }

        if (!response!.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Body onto Data, not the message — same reasoning as
            // OllamaVisionClient: the request body is a page of the user's mail
            // and an echoing error would put it in the exception text.
            var ex = new HttpRequestException(
                $"mistral-ocr {_route} failed {(int)response.StatusCode}.", inner: null, statusCode: response.StatusCode);
            ex.Data["body"] = Truncate(body, 400);
            response.Dispose();
            throw ex;
        }

        var parsed = await response.Content.ReadFromJsonAsync<OcrResponse>(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("mistral-ocr returned an empty body.");
        response.Dispose();

        // Order by the service's own page index rather than trusting array
        // order — page N of the output must line up with page N of the sample
        // or every per-page score is silently comparing different pages.
        return parsed.Pages is null
            ? []
            : [.. parsed.Pages.OrderBy(p => p.Index).Select(p => p.Markdown ?? string.Empty)];
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose() => _http.Dispose();

    private sealed class OcrRequest
    {
        [JsonPropertyName("model")]    public required string Model { get; init; }
        [JsonPropertyName("document")] public required DocumentRef Document { get; init; }
    }

    private sealed class DocumentRef
    {
        [JsonPropertyName("type")] public required string Type { get; init; }

        [JsonPropertyName("document_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DocumentUrl { get; init; }

        [JsonPropertyName("image_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ImageUrl { get; init; }
    }

    private sealed class OcrResponse
    {
        [JsonPropertyName("pages")] public List<OcrPage>? Pages { get; init; }
    }

    private sealed class OcrPage
    {
        [JsonPropertyName("index")]    public int Index { get; init; }
        [JsonPropertyName("markdown")] public string? Markdown { get; init; }
    }
}
