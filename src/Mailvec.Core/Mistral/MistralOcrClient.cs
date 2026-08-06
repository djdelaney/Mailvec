using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mailvec.Core.Options;
using Mailvec.Core.Vision;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Mistral;

/// <summary>
/// Hosted mistral-ocr as an <see cref="IVisionClient"/>. Works against
/// api.mistral.ai and Azure AI Foundry alike — only the route, model/deployment
/// name and auth header differ (see <see cref="MistralVisionOptions"/>).
///
/// Page-mode only, deliberately. mistral-ocr also accepts a whole PDF and
/// returns per-page markdown, but a bake-off over 71 real pages measured the two
/// modes as equivalent (token F1 0.971 vs 0.970 against the PDF text layer,
/// 0.917 agreement with each other) — so consuming the same rendered JPEGs the
/// Ollama path uses buys identical quality with no change to the OCR pass, no
/// second seam method, and lower latency on real scans. See
/// docs/contributing/ocr-bakeoff-2026-08-06.md.
///
/// Configure the HttpClient (BaseAddress, timeout, auth header) via DI; this
/// class does not own its lifetime.
/// </summary>
public sealed class MistralOcrClient(
    HttpClient http, IOptions<VisionOptions> options, ILogger<MistralOcrClient> logger) : IVisionClient
{
    private readonly MistralVisionOptions _opts = options.Value.Mistral;
    private DateTimeOffset _nextCallAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Image embeds mistral-ocr emits for picture regions, e.g.
    /// <c>![img-0.jpeg](img-0.jpeg)</c>. They are layout markers, not text.
    /// </summary>
    private static readonly Regex ImagePlaceholder = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);

    /// <summary>
    /// Scanned page: the whole image is a document, so everything comes back.
    /// </summary>
    public Task<string> OcrAsync(byte[] image, CancellationToken ct = default) =>
        TranscribeAsync(image, ct);

    /// <summary>
    /// Image attachment, which may legitimately hold no text at all.
    ///
    /// The Ollama path solves this with a prompt sentinel; mistral-ocr takes no
    /// prompt, and instead returns image placeholders and nothing else for a
    /// textless picture (verified on a real corpus photo). <see
    /// cref="StripPlaceholders"/> is therefore **load-bearing, not cosmetic**:
    /// unstripped, the caller sees non-empty text, writes it back as
    /// status='ocr', and a photograph becomes "searchable" by the literal string
    /// "![img-0.jpeg](img-0.jpeg)" — while MarkAttachmentImageNoText, the thing
    /// that should have fired, never does.
    /// </summary>
    public Task<string> OcrImageAsync(byte[] image, CancellationToken ct = default) =>
        TranscribeAsync(image, ct);

    private async Task<string> TranscribeAsync(byte[] image, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);

        var pages = await PostAsync(new OcrRequest
        {
            Model = _opts.Model,
            Document = new DocumentRef
            {
                Type = "image_url",
                ImageUrl = $"data:image/jpeg;base64,{Convert.ToBase64String(image)}",
            },
        }, ct).ConfigureAwait(false);

        var text = StripPlaceholders(string.Join("\n\n", pages));

        if (string.IsNullOrWhiteSpace(text))
        {
            // Legitimate and handled by the caller (blank page, or a textless
            // photo) — Debug, matching OllamaVisionClient.
            logger.LogDebug("mistral-ocr returned no text from model {Model}.", _opts.Model);
            return string.Empty;
        }

        return Cap(text);
    }

    /// <summary>
    /// Drop image embeds and collapse the whitespace they leave behind. A
    /// response that was ONLY placeholders becomes empty, which is exactly what
    /// the caller needs to mark the attachment as having no text.
    /// </summary>
    internal static string StripPlaceholders(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var stripped = ImagePlaceholder.Replace(text, string.Empty);
        // Collapse the blank lines the removals leave, then trim. Done on lines
        // rather than with a global whitespace regex so real table/heading
        // structure inside the text survives.
        var kept = stripped
            .Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0);
        return string.Join("\n", kept).Trim();
    }

    /// <summary>
    /// Bound what one call can contribute to the index.
    ///
    /// Ollama has num_predict; the hosted API has no equivalent, so the ceiling
    /// has to be applied here. It is not hypothetical: mistral-ocr repetition-
    /// looped on a dense architectural drawing, emitting one table row over and
    /// over. Note that CollapseRepeatedLines (the Ollama-side mitigation) would
    /// NOT have caught it — the repetition was inside a single pipe-table line —
    /// which is why this is a hard character cap rather than a port of that.
    /// </summary>
    private string Cap(string text)
    {
        if (_opts.MaxCharsPerCall <= 0 || text.Length <= _opts.MaxCharsPerCall) return text;
        logger.LogWarning(
            "mistral-ocr returned {Chars} chars, over the {Cap} cap; truncating (possible repetition loop).",
            text.Length, _opts.MaxCharsPerCall);
        return text[.._opts.MaxCharsPerCall];
    }

    private async Task<IReadOnlyList<string>> PostAsync(OcrRequest request, CancellationToken ct)
    {
        // Buffered StringContent, not PostAsJsonAsync: the latter streams, which
        // sends Transfer-Encoding: chunked with no Content-Length, and the Azure
        // AI Foundry gateway rejects that outright ("no_content_length_header")
        // before the model sees the request.
        var json = JsonSerializer.Serialize(request, SerializerOptions);

        HttpResponseMessage? response = null;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var gap = _nextCallAt - DateTimeOffset.UtcNow;
                if (gap > TimeSpan.Zero) await Task.Delay(gap, ct).ConfigureAwait(false);

                // Fresh content per attempt — HttpContent is single-use and
                // reusing it across a retry throws rather than resending.
                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                response?.Dispose();
                response = await SendAsync(content, ct).ConfigureAwait(false);
                _nextCallAt = DateTimeOffset.UtcNow.AddMilliseconds(_opts.MinIntervalMs);

                if (response.IsSuccessStatusCode) break;

                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests
                    || (int)response.StatusCode >= 500;
                if (!retryable || attempt >= _opts.MaxRetries)
                {
                    await ThrowClassifiedAsync(response, ct).ConfigureAwait(false);
                }

                var delay = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, attempt)));
                logger.LogInformation(
                    "mistral-ocr {Status}; retrying in {Delay:0.#}s (attempt {Attempt}/{Max}).",
                    (int)response.StatusCode, delay.TotalSeconds, attempt + 1, _opts.MaxRetries);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            var parsed = await response!.Content.ReadFromJsonAsync<OcrResponse>(ct).ConfigureAwait(false)
                ?? throw new VisionException(VisionFailureKind.Transient, "mistral-ocr returned an empty body.");

            return parsed.Pages is null
                ? []
                : [.. parsed.Pages.OrderBy(p => p.Index).Select(p => p.Markdown ?? string.Empty)];
        }
        finally
        {
            response?.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpContent content, CancellationToken ct)
    {
        try
        {
            return await http.PostAsync(_opts.Route, content, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine shutdown
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient timeout surfaces as TaskCanceledException with the
            // outer token un-cancelled. Retry-worthy, never a document verdict.
            throw new VisionException(VisionFailureKind.Transient, "mistral-ocr call timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new VisionException(VisionFailureKind.Transient, "mistral-ocr is unreachable.", ex);
        }
    }

    /// <summary>
    /// Map an HTTP status onto the failure taxonomy. The distinctions here are
    /// the whole point of this class from the OCR pass's perspective — see
    /// <see cref="VisionFailureKind"/> for what each one costs if it's wrong.
    /// </summary>
    private async Task ThrowClassifiedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var kind = response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => VisionFailureKind.Backpressure,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                => VisionFailureKind.AuthOrConfig,
            HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType
                => VisionFailureKind.DocumentFatal,
            // 422 is the shape the service uses for "this payload is wrong",
            // which for a fixed request envelope means the document. 400 is
            // ambiguous but lands the same way — and DocumentFatal retires only
            // this one attachment, so a misclassification here is bounded.
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
                => VisionFailureKind.DocumentFatal,
            _ => VisionFailureKind.Transient,
        };

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        // Body onto Data rather than into the message — the request was a
        // rendered page of the user's mail, and an echoing error would put it
        // into the logs. Same rule as OllamaVisionClient.
        var ex = new VisionException(kind, $"mistral-ocr {_opts.Route} failed {status}.");
        ex.Data["body"] = body.Length <= 400 ? body : body[..400] + "…";
        throw ex;
    }

    /// <summary>
    /// Reachability + credential check, used to gate the OCR batch and by
    /// <c>mailvec doctor</c>.
    ///
    /// Deliberately posts a request with no <c>document</c> field: a live route
    /// with good credentials answers 422 ("body.document field required"), a bad
    /// key answers 401/403, and a wrong route answers 404. That distinguishes
    /// all three **without submitting any mail content and without incurring a
    /// billable page** — which matters because this runs on every OCR cycle.
    /// A blank-image probe (the Ollama equivalent) would do neither.
    /// </summary>
    public async Task<bool> IsModelAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(new { model = _opts.Model }, SerializerOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(_opts.Route, content, ct).ConfigureAwait(false);

            // 422 means the route exists and accepted our credentials, then
            // rejected the deliberately-incomplete body. That is success here.
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity) return true;
            if (response.IsSuccessStatusCode) return true;

            logger.LogWarning(
                "mistral-ocr probe returned {Status} for {Endpoint}/{Route} (model {Model}).",
                (int)response.StatusCode, http.BaseAddress, _opts.Route, _opts.Model);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "mistral-ocr probe failed for {Endpoint}.", http.BaseAddress);
            return false;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Applies the configured auth scheme to a client at DI time.</summary>
    public static void ApplyAuth(HttpClient client, MistralVisionOptions opts)
    {
        if (opts.AuthHeader.Equals("bearer", StringComparison.OrdinalIgnoreCase))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);
        else
            client.DefaultRequestHeaders.Add(opts.AuthHeader, opts.ApiKey);
    }

    private sealed class OcrRequest
    {
        [JsonPropertyName("model")]    public required string Model { get; init; }
        [JsonPropertyName("document")] public required DocumentRef Document { get; init; }
    }

    private sealed class DocumentRef
    {
        [JsonPropertyName("type")] public required string Type { get; init; }

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
