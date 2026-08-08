using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Mailvec.Core.Embedding;

/// <summary>
/// The hosted protocol transport: `POST /v1/embeddings`-style request, indexed
/// `data[].embedding` response envelope. Fireworks, OpenAI, Baseten BEI and
/// compatible custom deployments are all PROFILES over this one class —
/// capability differences (send/omit model, send/omit dimensions, encoding
/// format) ride the resolved profile, never per-vendor subclasses.
///
/// Transport contract only: serialize, one protocol request, classify,
/// return indexed RAW vectors. The mathematical contract (width, finiteness,
/// L2 normalization — load-bearing here: Fireworks Qwen3 returns norms ~65)
/// belongs to EmbeddingService. Auth and redirect policy are applied by
/// registration onto the HttpClient: bearer header, AllowAutoRedirect=false
/// (no legitimate inference call redirects, and a redirect must not receive
/// the credential or a mail payload).
///
/// Upstream response bodies NEVER appear in exception messages or logs —
/// provider errors can echo their input, and the input is mail content.
/// </summary>
public sealed class OpenAiCompatibleTransport(
    HttpClient http,
    ResolvedEmbeddingProfile profile,
    IEmbeddingTelemetryObserver? telemetry = null) : IEmbeddingTransport
{
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0) return [];
        foreach (var input in inputs)
        {
            // The provider contract disallows empty strings; the chunker
            // normally prevents them, but a hosted 400 for one would be
            // indistinguishable from a config error. Refuse locally.
            if (string.IsNullOrEmpty(input))
                throw new EmbeddingException(EmbeddingFailureKind.InvalidResponse,
                    "Refusing to send an empty input string to the embeddings endpoint.");
        }

        var request = new EmbedRequest
        {
            Model = profile.SendWireModel ? profile.WireModel : null,
            Input = inputs,
            Dimensions = profile.SendDimensions ? profile.OutputDimensions : null,
            EncodingFormat = profile.EncodingFormat,
        };

        try
        {
            // BaseAddress is the validated full endpoint URL — no
            // provider-specific path composition here.
            using var response = await http.PostAsJsonAsync("", request, JsonOpts, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await ClassifyAsync(response, ct).ConfigureAwait(false);

            var parsed = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct).ConfigureAwait(false)
                ?? throw new EmbeddingException(EmbeddingFailureKind.InvalidResponse,
                    "Embeddings endpoint returned an empty body.");
            Observe(response, parsed);
            return Reassemble(parsed, inputs.Count);
        }
        catch (EmbeddingException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            throw new EmbeddingException(EmbeddingFailureKind.Transient,
                "Embeddings request timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new EmbeddingException(EmbeddingFailureKind.Transient,
                "Embeddings endpoint connection failed.", ex);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new EmbeddingException(EmbeddingFailureKind.InvalidResponse,
                "Embeddings endpoint returned unparseable JSON.", ex);
        }
    }

    /// <summary>
    /// Never trust response array order: require exactly one data item per
    /// input, every index unique and in range, and reassemble by index.
    /// </summary>
    private static float[][] Reassemble(EmbedResponse parsed, int inputCount)
    {
        if (parsed.Data is null || parsed.Data.Length != inputCount)
            throw new EmbeddingException(EmbeddingFailureKind.InvalidResponse,
                $"Embeddings endpoint returned {parsed.Data?.Length ?? 0} items for {inputCount} inputs.");

        var vectors = new float[inputCount][];
        foreach (var item in parsed.Data)
        {
            if (item.Index is not { } idx || idx < 0 || idx >= inputCount)
                throw new EmbeddingException(EmbeddingFailureKind.InvalidResponse,
                    $"Embeddings endpoint returned an out-of-range or missing index ({item.Index?.ToString() ?? "null"}).");
            if (vectors[idx] is not null)
                throw new EmbeddingException(EmbeddingFailureKind.InvalidResponse,
                    $"Embeddings endpoint returned duplicate index {idx}.");
            vectors[idx] = item.Embedding
                ?? throw new EmbeddingException(EmbeddingFailureKind.InvalidResponse,
                    $"Embeddings endpoint returned no embedding at index {idx}.");
        }
        return vectors;
    }

    /// <summary>
    /// Status-code classification per the proposal's table. The body is read
    /// only to positively identify a context-length 400 — hosted providers
    /// have no `truncate` flag and no sentinel — and is then discarded; it
    /// never travels in the exception. There is deliberately NO split/truncate
    /// fallback here: current chunks cap at ~930 chars against a 32k-token
    /// window, so a genuine overflow means something upstream broke and must
    /// surface loudly rather than be silently truncated.
    /// </summary>
    private static async Task<EmbeddingException> ClassifyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = response.StatusCode;
        var kind = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => EmbeddingFailureKind.AuthOrConfig,
            HttpStatusCode.NotFound => EmbeddingFailureKind.ModelUnavailable,
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => EmbeddingFailureKind.Backpressure,
            HttpStatusCode.BadRequest => EmbeddingFailureKind.AuthOrConfig, // malformed/unsupported — no blind retry
            _ => EmbeddingFailureKind.Transient,
        };

        if (status == HttpStatusCode.BadRequest)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Contains("context", StringComparison.OrdinalIgnoreCase)
                || body.Contains("maximum length", StringComparison.OrdinalIgnoreCase)
                || body.Contains("too long", StringComparison.OrdinalIgnoreCase)
                || body.Contains("max_tokens", StringComparison.OrdinalIgnoreCase))
            {
                kind = EmbeddingFailureKind.InputTooLong;
            }
        }

        return new EmbeddingException(kind, $"Embeddings endpoint returned {(int)status}.");
    }

    /// <summary>
    /// Telemetry is best-effort by contract: every field optional, absence
    /// is normal, and an observer exception must never fail an embed that
    /// already succeeded. Never contains inputs or credentials.
    /// </summary>
    private void Observe(HttpResponseMessage response, EmbedResponse parsed)
    {
        if (telemetry is null) return;
        try
        {
            static long? Header(HttpResponseMessage r, params string[] names)
            {
                foreach (var n in names)
                    if (r.Headers.TryGetValues(n, out var vals)
                        && long.TryParse(vals.FirstOrDefault(), out var parsedVal))
                        return parsedVal;
                return null;
            }
            string? requestId = null;
            foreach (var n in (string[])["x-request-id", "request-id"])
                if (response.Headers.TryGetValues(n, out var vals)) { requestId = vals.FirstOrDefault(); break; }

            telemetry.OnEmbeddingResponse(new EmbeddingTelemetry(
                PromptTokens: parsed.Usage?.PromptTokens,
                ResponseModel: parsed.Model,
                RequestId: requestId,
                RateLimitRemainingRequests: Header(response, "x-ratelimit-remaining-requests"),
                RateLimitRemainingTokens: Header(response, "x-ratelimit-remaining-tokens")));
        }
        catch
        {
            // Observation must never take down the embed it observed.
        }
    }

    /// <summary>
    /// No model-catalog requirement for hosted profiles (a deployment-scoped
    /// endpoint need not offer one) — null keeps the probe's refinement
    /// honest: unknown, never a missing-model claim.
    /// </summary>
    public Task<bool?> IsModelAvailableAsync(CancellationToken ct = default) => Task.FromResult<bool?>(null);

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class EmbedRequest
    {
        [JsonPropertyName("model")] public string? Model { get; init; }
        [JsonPropertyName("input")] public required IReadOnlyList<string> Input { get; init; }
        [JsonPropertyName("dimensions")] public int? Dimensions { get; init; }
        [JsonPropertyName("encoding_format")] public string? EncodingFormat { get; init; }
    }

    private sealed class EmbedResponse
    {
        [JsonPropertyName("data")] public EmbedItem[]? Data { get; init; }
        [JsonPropertyName("model")] public string? Model { get; init; }
        [JsonPropertyName("usage")] public EmbedUsage? Usage { get; init; }
    }

    private sealed class EmbedUsage
    {
        [JsonPropertyName("prompt_tokens")] public int? PromptTokens { get; init; }
    }

    private sealed class EmbedItem
    {
        [JsonPropertyName("index")] public int? Index { get; init; }
        [JsonPropertyName("embedding")] public float[]? Embedding { get; init; }
    }
}
