using System.Net.Http.Json;
using System.Text;
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
    // structure preserved, no commentary. Assumes the image is a document page
    // that contains text.
    private const string OcrPrompt =
        "You are an OCR engine. Transcribe ALL text from this scanned document exactly as it " +
        "appears, preserving structure (headings, fields, labels, tables) as best you can. " +
        "Output only the transcribed text, no commentary.";

    // Image-attachment variant: a photo/screenshot may legitimately contain no
    // text. The escape hatch is a fixed sentinel rather than "output nothing" —
    // asked to emit nothing, the model narrates the absence ("nothing here"),
    // which would itself get indexed. A sentinel it *does* reliably emit, which
    // OcrImageAsync maps back to empty, is deterministic (verified against a
    // real corpus sample: textless photos that hallucinated a stray word under
    // the document prompt now return the sentinel).
    private const string ImageNoTextSentinel = "NO_TEXT_FOUND";
    private const string ImageOcrPrompt =
        "You are an OCR engine. Transcribe ALL text visible in this image exactly as it appears, " +
        "preserving structure (headings, fields, labels, tables) as best you can. " +
        "Output only the transcribed text, no commentary. " +
        "If the image contains no readable text at all, reply with exactly " + ImageNoTextSentinel + " and nothing else.";

    public Task<string> OcrAsync(byte[] image, CancellationToken ct = default) =>
        GenerateAsync(image, OcrPrompt, ct);

    public async Task<string> OcrImageAsync(byte[] image, CancellationToken ct = default)
    {
        var text = await GenerateAsync(image, ImageOcrPrompt, ct).ConfigureAwait(false);
        // Collapse the "no readable text" sentinel to empty so the caller's
        // empty-check marks the attachment no_text instead of indexing a marker.
        return text.Trim() == ImageNoTextSentinel ? string.Empty : text;
    }

    private async Task<string> GenerateAsync(byte[] image, string prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);

        var request = new GenerateRequest
        {
            Model = _opts.VisionModel,
            Prompt = prompt,
            Images = [Convert.ToBase64String(image)],
            Stream = false,
            KeepAlive = string.IsNullOrEmpty(_opts.VisionKeepAlive) ? null : _opts.VisionKeepAlive,
            Options = new GenerateOptions
            {
                Temperature = 0,
                // Bound generation so a repetition-looping vision model can't run
                // for minutes and blow the HttpClient timeout (which throws
                // TaskCanceledException and stalls the OCR batch).
                NumPredict = _opts.VisionMaxTokens > 0 ? _opts.VisionMaxTokens : null,
            },
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

        // Empty is a legitimate, handled outcome (blank page, or a textless
        // photo hitting the image prompt's escape hatch) — the caller decides
        // what to persist, so this is Debug, not a warning.
        if (string.IsNullOrWhiteSpace(parsed.Response))
            logger.LogDebug("Vision OCR returned empty text from model {Model}.", _opts.VisionModel);

        return CollapseRepeatedLines(parsed.Response ?? string.Empty);
    }

    /// <summary>
    /// Squash runs of an identical consecutive line down to one occurrence.
    /// Vision models sometimes fall into a repetition loop on noisy images —
    /// emitting the same line hundreds of times until num_predict cuts them off
    /// (e.g. a photo of a banner producing "Colonial" ×2000). The num_predict cap
    /// bounds the *time*; this keeps the degenerate output from bloating the
    /// search index, while preserving the real text that usually precedes the
    /// loop. Legitimate OCR almost never repeats a full line back-to-back, so
    /// this is safe for good output.
    /// </summary>
    internal static string CollapseRepeatedLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length);
        string? prevKept = null;
        foreach (var raw in text.Split('\n'))
        {
            var trimmed = raw.TrimEnd();
            if (trimmed.Length > 0 && trimmed == prevKept) continue; // drop a repeat of the last kept line
            sb.Append(raw).Append('\n');
            if (trimmed.Length > 0) prevKept = trimmed;
        }
        return sb.ToString().TrimEnd('\n');
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

        [JsonPropertyName("num_predict")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? NumPredict { get; init; }
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
