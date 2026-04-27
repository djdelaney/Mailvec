using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Mailvec.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Ollama;

/// <summary>
/// Wrapper around Ollama's POST /api/embed with built-in fallback for
/// context-length 400s: oversize batches are split, and a single input that
/// still doesn't fit is progressively truncated until it does. Configure the
/// underlying HttpClient (BaseAddress, timeout, resilience) via DI; this class
/// does not own the HttpClient lifetime.
/// </summary>
public sealed class OllamaClient(HttpClient http, IOptions<OllamaOptions> options, ILogger<OllamaClient> logger)
{
    private readonly OllamaOptions _opts = options.Value;

    // Hard floor on truncation. If a single input still 400s when this small,
    // something else is wrong (model not loaded, GPU OOM) and we surface the error.
    private const int MinTruncatedChars = 64;

    /// <summary>
    /// Returns one float[] per input string, in the same order. May silently
    /// truncate inputs that exceed the model's context length — log warnings
    /// surface this. Throws on any non-recoverable Ollama error.
    /// </summary>
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0) return [];

        var (vectors, error) = await TryEmbedAsync(inputs, ct).ConfigureAwait(false);
        if (vectors is not null) return vectors;

        // Server returned a context-length 400. Recover by splitting / truncating.
        if (!IsContextLengthError(error)) throw error!;

        return await EmbedWithFallbackAsync(inputs, ct).ConfigureAwait(false);
    }

    private async Task<float[][]> EmbedWithFallbackAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        if (inputs.Count > 1)
        {
            // Split the failed batch in half and recurse. The actual oversize
            // input(s) eventually become singletons that hit the truncation path.
            var mid = inputs.Count / 2;
            var leftInputs = inputs.Take(mid).ToArray();
            var rightInputs = inputs.Skip(mid).ToArray();

            var left = await EmbedAsync(leftInputs, ct).ConfigureAwait(false);
            var right = await EmbedAsync(rightInputs, ct).ConfigureAwait(false);

            var merged = new float[inputs.Count][];
            left.CopyTo(merged, 0);
            right.CopyTo(merged, mid);
            return merged;
        }

        // Singleton too long. Truncate by half repeatedly until it fits.
        var input = inputs[0];
        var truncated = input;
        while (truncated.Length > MinTruncatedChars)
        {
            truncated = truncated[..(truncated.Length / 2)];
            logger.LogWarning(
                "Ollama input over context length; truncating from {Original} to {Truncated} chars and retrying.",
                input.Length, truncated.Length);

            var (vectors, error) = await TryEmbedAsync([truncated], ct).ConfigureAwait(false);
            if (vectors is not null) return vectors;
            if (!IsContextLengthError(error)) throw error!;
        }

        throw new InvalidOperationException(
            $"Ollama rejected input as too long even after truncation to {MinTruncatedChars} chars (original {input.Length}).");
    }

    /// <summary>
    /// Sends a single /api/embed request. Returns vectors on success, the
    /// exception on a recoverable HTTP failure, or throws for non-HTTP errors
    /// (deserialisation, schema mismatch).
    /// </summary>
    private async Task<(float[][]? Vectors, HttpRequestException? Error)> TryEmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var request = new EmbedRequest
        {
            Model = _opts.EmbeddingModel,
            Input = inputs,
            KeepAlive = _opts.KeepAlive,
            // Ollama's truncate flag is buggy in batched mode in current
            // versions, so we don't rely on it; sent for forward-compat.
            Truncate = true,
        };

        using var response = await http.PostAsJsonAsync("/api/embed", request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var ex = new HttpRequestException(
                $"Ollama /api/embed failed {(int)response.StatusCode}: {body}",
                inner: null,
                statusCode: response.StatusCode);
            // Tag the body onto Data so callers can sniff for context-length errors.
            ex.Data["body"] = body;
            return (null, ex);
        }

        var parsed = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Ollama returned an empty body.");

        if (parsed.Embeddings is null || parsed.Embeddings.Length != inputs.Count)
        {
            throw new InvalidOperationException(
                $"Ollama returned {parsed.Embeddings?.Length ?? 0} embeddings for {inputs.Count} inputs.");
        }

        var expected = _opts.EmbeddingDimensions;
        for (int i = 0; i < parsed.Embeddings.Length; i++)
        {
            var vec = parsed.Embeddings[i];
            if (vec.Length != expected)
            {
                throw new InvalidOperationException(
                    $"Embedding {i} has {vec.Length} dimensions; expected {expected} for model {_opts.EmbeddingModel}.");
            }
        }

        return (parsed.Embeddings, null);
    }

    private static bool IsContextLengthError(HttpRequestException? ex)
    {
        if (ex is null) return false;
        if (ex.StatusCode != HttpStatusCode.BadRequest) return false;
        var body = ex.Data["body"] as string;
        return body is not null && body.Contains("context length", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class EmbedRequest
    {
        [JsonPropertyName("model")]      public required string Model { get; init; }
        [JsonPropertyName("input")]      public required IReadOnlyList<string> Input { get; init; }
        [JsonPropertyName("keep_alive")] public string? KeepAlive { get; init; }
        [JsonPropertyName("truncate")]   public bool? Truncate { get; init; }
    }

    private sealed class EmbedResponse
    {
        [JsonPropertyName("model")]      public string? Model { get; init; }
        [JsonPropertyName("embeddings")] public float[][]? Embeddings { get; init; }
    }
}
