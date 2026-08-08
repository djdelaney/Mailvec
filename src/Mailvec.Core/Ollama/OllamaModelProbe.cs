using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Mailvec.Core.Ollama;

/// <summary>
/// Shared GET /api/tags probe: is a given model pulled on the Ollama server?
/// Used by both the embedding and vision clients so the name-matching rule
/// (exact, tagless-config-means-":latest", or any tag of the same base name)
/// can't drift between them. Tri-state result: true/false when the server
/// answered with a model list, null when the tags call itself failed — which
/// is what lets callers distinguish "server down" from "server up but the
/// model isn't pulled". Bounded by a 5s internal timeout.
/// </summary>
internal static class OllamaModelProbe
{
    internal static async Task<bool?> IsModelAvailableAsync(HttpClient http, string model, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await http.GetAsync("/api/tags", cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var tags = await response.Content.ReadFromJsonAsync<TagsResponse>(cts.Token).ConfigureAwait(false);
            if (tags?.Models is null) return null;

            return tags.Models.Any(m => Matches(m.Name, model));
        }
        catch (HttpRequestException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>
    /// The listed digest of the given model, or null when the server was
    /// unreachable, answered malformed, or doesn't list the model.
    ///
    /// <para>Deliberately STRICTER than <see cref="Matches"/>: an explicit
    /// tag resolves only its exact tag, a tagless config resolves exact name
    /// then <c>name:latest</c> — the same resolution an embed request gets —
    /// and there is NO fallback to an arbitrary same-base tag. The broad rule
    /// answers "is some tag of this model pulled?" (availability); artifact
    /// identity must name the artifact that actually serves. With
    /// <c>model:old</c> and <c>model:latest</c> both installed, the broad rule
    /// let list ordering decide which digest got stamped — false drift when
    /// the order changed, or a stale tag watched while <c>:latest</c> moved.</para>
    /// </summary>
    internal static async Task<string?> GetModelDigestAsync(HttpClient http, string model, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await http.GetAsync("/api/tags", cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var tags = await response.Content.ReadFromJsonAsync<TagsResponse>(cts.Token).ConfigureAwait(false);
            if (tags?.Models is not { } models) return null;

            var match = models.FirstOrDefault(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase))
                ?? models.FirstOrDefault(m => string.Equals(m.Name, model + ":latest", StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(match?.Digest) ? null : match.Digest;
        }
        catch (HttpRequestException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    // Ollama lists models as "name:tag" (e.g. "qwen2.5vl:7b"). Match the
    // configured name exactly, or treat a tagless config as ":latest", or
    // any tag of the same base name.
    internal static bool Matches(string? listed, string want) =>
        listed is { } n &&
        (string.Equals(n, want, StringComparison.OrdinalIgnoreCase)
         || string.Equals(n, want + ":latest", StringComparison.OrdinalIgnoreCase)
         || n.StartsWith(want + ":", StringComparison.OrdinalIgnoreCase));

    private sealed class TagsResponse
    {
        [JsonPropertyName("models")] public TagModel[]? Models { get; init; }
    }

    private sealed class TagModel
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("digest")] public string? Digest { get; init; }
    }
}
