using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Mailvec.Core.Options;
using Mailvec.Core.Vision;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Ollama;

/// <summary>
/// Ollama-backed <see cref="IVisionClient"/>: OCRs a page image via POST
/// /api/generate with the configured vision model. Configure the underlying
/// HttpClient (BaseAddress, the longer vision timeout) via DI; this class does
/// not own the HttpClient lifetime. The 6GB vision model is loaded on demand —
/// see <see cref="OllamaOptions.VisionKeepAlive"/>.
/// </summary>
public sealed class OllamaVisionClient(HttpClient http, IOptions<OllamaOptions> options, ILogger<OllamaVisionClient> logger) : IVisionClient
{
    private readonly OllamaOptions _opts = options.Value;

    // Prompt validated in the OCR spike (qwen2.5vl): verbatim transcription,
    // structure preserved, no commentary.
    private const string OcrPrompt =
        "You are an OCR engine. Transcribe ALL text from this scanned document exactly as it " +
        "appears, preserving structure (headings, fields, labels, tables) as best you can. " +
        "Output only the transcribed text, no commentary.";

    public async Task<string> OcrAsync(byte[] image, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        var request = new GenerateRequest
        {
            Model = _opts.VisionModel,
            Prompt = OcrPrompt,
            Images = [Convert.ToBase64String(image)],
            Stream = false,
            KeepAlive = string.IsNullOrEmpty(_opts.VisionKeepAlive) ? null : _opts.VisionKeepAlive,
            Options = new GenerateOptions { Temperature = 0 },
        };

        using var response = await http.PostAsJsonAsync("/api/generate", request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Ollama /api/generate failed {(int)response.StatusCode}: {body}", inner: null, statusCode: response.StatusCode);
        }

        var parsed = await response.Content.ReadFromJsonAsync<GenerateResponse>(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Ollama returned an empty body.");

        if (string.IsNullOrWhiteSpace(parsed.Response))
            logger.LogWarning("Vision OCR returned empty text from model {Model}.", _opts.VisionModel);

        return parsed.Response ?? string.Empty;
    }

    public async Task<bool> IsModelAvailableAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await http.GetAsync("/api/tags", cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;

            var tags = await response.Content.ReadFromJsonAsync<TagsResponse>(cts.Token).ConfigureAwait(false);
            if (tags?.Models is null) return false;

            // Ollama lists models as "name:tag" (e.g. "qwen2.5vl:7b"). Match the
            // configured name exactly, or treat a tagless config as ":latest",
            // or any tag of the same base name.
            var want = _opts.VisionModel;
            return tags.Models.Any(m => m.Name is { } n &&
                (string.Equals(n, want, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(n, want + ":latest", StringComparison.OrdinalIgnoreCase)
                 || n.StartsWith(want + ":", StringComparison.OrdinalIgnoreCase)));
        }
        catch (HttpRequestException) { return false; }
        catch (System.Text.Json.JsonException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    private sealed class GenerateRequest
    {
        [JsonPropertyName("model")]      public required string Model { get; init; }
        [JsonPropertyName("prompt")]     public required string Prompt { get; init; }
        [JsonPropertyName("images")]     public required IReadOnlyList<string> Images { get; init; }
        [JsonPropertyName("stream")]     public bool Stream { get; init; }
        // Omit entirely when null so Ollama applies its own default keep_alive
        // (and unloads the heavy model when idle) rather than receiving "null".
        [JsonPropertyName("keep_alive")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? KeepAlive { get; init; }
        [JsonPropertyName("options")]    public GenerateOptions? Options { get; init; }
    }

    private sealed class GenerateOptions
    {
        [JsonPropertyName("temperature")] public double Temperature { get; init; }
    }

    private sealed class GenerateResponse
    {
        [JsonPropertyName("response")] public string? Response { get; init; }
    }

    private sealed class TagsResponse
    {
        [JsonPropertyName("models")] public TagModel[]? Models { get; init; }
    }

    private sealed class TagModel
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
    }
}
