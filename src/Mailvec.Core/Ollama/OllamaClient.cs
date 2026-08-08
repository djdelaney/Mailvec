using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Mailvec.Core.Embedding;
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
public sealed class OllamaClient(HttpClient http, IOptions<OllamaOptions> options, ILogger<OllamaClient> logger) : IEmbeddingClient
{
    private readonly OllamaOptions _opts = options.Value;

    // Hard floor on truncation. If a single input still 400s when this small,
    // something else is wrong (model not loaded, GPU OOM) and we surface the error.
    private const int MinTruncatedChars = 64;

    /// <summary>
    /// Readiness check — sends a minimal /api/embed against the *configured*
    /// model and confirms a non-empty vector comes back. This is deliberately
    /// stronger than a GET /api/tags liveness ping: Ollama answers /api/tags
    /// with 200 even when the model can't actually load (incomplete/wrong
    /// build, missing runner, GPU OOM), and that exact "reachable but can't
    /// embed" state silently wedges the embedder while leaving /health green.
    /// A real embed is the only signal that "reachable" also means "ready".
    ///
    /// Bounded by a short internal timeout so the MCP health endpoint can't
    /// hang on the shared embedder HttpClient's 60s timeout — 5s allows for a
    /// cold model load on the first probe (subsequent probes hit a warm model
    /// kept resident by KeepAlive). Don't raise it: /health is the mcp
    /// container's compose healthcheck, which times out at 10s, and this probe
    /// plus the 2s /api/tags follow-up already spend most of that. Returns
    /// false on any error; does not surface detail.
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var request = new EmbedRequest
            {
                Model = _opts.EmbeddingModel,
                Input = ["ping"],
                KeepAlive = _opts.KeepAlive,
                Truncate = true,
            };
            using var response = await http.PostAsJsonAsync("/api/embed", request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            var parsed = await response.Content.ReadFromJsonAsync<EmbedResponse>(cts.Token).ConfigureAwait(false);
            return parsed?.Embeddings is { Length: > 0 } embeddings && embeddings[0].Length > 0;
        }
        catch (HttpRequestException) { return false; }
        catch (System.Text.Json.JsonException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    /// <summary>
    /// Tri-state /api/tags probe for the configured embedding model. Weaker
    /// than <see cref="PingAsync"/> (a listed model can still fail to load),
    /// but it's what distinguishes "server down" (null) from "server up, model
    /// not pulled" (false) when the ping fails — the two states need opposite
    /// remediation, and conflating them sends users restarting a healthy Ollama.
    /// </summary>
    public Task<bool?> IsModelAvailableAsync(CancellationToken ct = default) =>
        OllamaModelProbe.IsModelAvailableAsync(http, _opts.EmbeddingModel, ct);

    /// <summary>
    /// Ollama exposes each tag's content-addressed manifest digest via
    /// /api/tags — the artifact-pinning half of the stability hybrid. Null
    /// when unreachable or unlisted (unknown, never drift).
    /// </summary>
    public Task<string?> GetModelArtifactDigestAsync(CancellationToken ct = default) =>
        OllamaModelProbe.GetModelDigestAsync(http, _opts.EmbeddingModel, ct);

    /// <summary>
    /// Returns one float[] per input string, in the same order. May silently
    /// truncate inputs that exceed the model's context length — log warnings
    /// surface this. Non-recoverable failures throw a classified
    /// <see cref="EmbeddingException"/> (the provider-neutral taxonomy):
    /// callers branch on <see cref="EmbeddingFailureKind"/>, never on
    /// HTTP-level exception types. Only a positively identified
    /// context-length 400 enters the split/truncate fallback — an auth,
    /// model, or malformed-request failure must surface immediately rather
    /// than being misdiagnosed as long input.
    /// </summary>
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0) return [];

        try
        {
            var (vectors, error) = await TryEmbedAsync(inputs, ct).ConfigureAwait(false);
            if (vectors is not null) return vectors;

            // Server returned a context-length 400. Recover by splitting / truncating.
            if (!IsContextLengthError(error)) throw Classify(error!);

            return await EmbedWithFallbackAsync(inputs, ct).ConfigureAwait(false);
        }
        catch (EmbeddingException) { throw; }               // already classified (incl. recursion)
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            // Timeout (HttpClient / resilience pipeline), not caller cancellation.
            throw new EmbeddingException(EmbeddingFailureKind.Transient,
                "Ollama /api/embed timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            // Network-level: connection refused, DNS, TLS. No status code.
            throw new EmbeddingException(EmbeddingFailureKind.Transient,
                "Ollama /api/embed connection failed.", ex);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new EmbeddingException(EmbeddingFailureKind.InvalidResponse,
                "Ollama /api/embed returned unparseable JSON.", ex);
        }
    }

    /// <summary>
    /// Status-code classification for a non-2xx /api/embed answer. The
    /// message carries the status code only — the body stays on Data, out of
    /// logs and MCP errors (it can echo mail content).
    /// </summary>
    private static EmbeddingException Classify(HttpRequestException error)
    {
        var kind = error.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => EmbeddingFailureKind.AuthOrConfig,
            HttpStatusCode.NotFound => EmbeddingFailureKind.ModelUnavailable,
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => EmbeddingFailureKind.Backpressure,
            _ => EmbeddingFailureKind.Transient,
        };
        return new EmbeddingException(kind, error.Message, error);
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
            if (!IsContextLengthError(error)) throw Classify(error!);
        }

        throw new EmbeddingException(EmbeddingFailureKind.InputTooLong,
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
            // Server-side truncation: works for batched /api/embed as of
            // Ollama 0.21.2. The 400-recovery path below is kept as a safety
            // net for older/regressed servers.
            Truncate = true,
        };

        using var response = await http.PostAsJsonAsync("/api/embed", request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // The body stays OUT of the message. Exception messages travel: they
            // reach durable logs via ex.ToString() and, from the search tool, an
            // MCP client — i.e. off-network. An Ollama error body is usually its
            // own JSON, but it is upstream-controlled and may echo the input,
            // which here is mail content. Status code alone is what callers act on.
            var ex = new HttpRequestException(
                $"Ollama /api/embed failed {(int)response.StatusCode}.",
                inner: null,
                statusCode: response.StatusCode);
            // Tag the body onto Data so callers can sniff for context-length
            // errors. Data is NOT rendered by ex.ToString(), so this keeps the
            // oversize-batch fallback working without the body leaking with it.
            ex.Data["body"] = body;
            return (null, ex);
        }

        var parsed = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct).ConfigureAwait(false)
            ?? throw new EmbeddingException(EmbeddingFailureKind.InvalidResponse, "Ollama returned an empty body.");

        if (parsed.Embeddings is null || parsed.Embeddings.Length != inputs.Count)
        {
            // Count is protocol framing, so it stays in the transport; the
            // MATHEMATICAL contract (dimension width, finiteness, L2
            // normalization) moved up into EmbeddingService, which owns it
            // once for every transport — don't reintroduce it here.
            throw new EmbeddingException(EmbeddingFailureKind.InvalidResponse,
                $"Ollama returned {parsed.Embeddings?.Length ?? 0} embeddings for {inputs.Count} inputs.");
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
