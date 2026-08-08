namespace Mailvec.Core.Embedding;

/// <summary>
/// The one implementation of <see cref="IEmbeddingService"/>: applies the
/// resolved profile's text policy, delegates the wire work to the
/// registered <see cref="IEmbeddingClient"/> transport, and classifies
/// readiness. Constructed by <see cref="EmbeddingRegistration"/> with the
/// same resolved profile in every executable — the text policy applied to
/// queries here and to documents in the embedder is one object, not two
/// config reads.
/// </summary>
public sealed class EmbeddingService(IEmbeddingClient client, ResolvedEmbeddingProfile profile) : IEmbeddingService
{
    /// <summary>
    /// Mirrors the old OllamaClient.PingAsync bound: 5s allows a cold model
    /// load; /health is the compose healthcheck with a 10s budget, so this
    /// plus the 2s tags refinement must stay well inside it.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeRefineTimeout = TimeSpan.FromSeconds(2);

    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        // Query-side instruction transform for asymmetric (instruction-tuned)
        // models. Documents are embedded with the document transform (empty
        // today); only the query carries the instruction — that's how these
        // models are trained. Empty prefix (mxbai et al.) embeds unchanged.
        var vectors = await client.EmbedAsync([profile.QueryPrefix + text], ct).ConfigureAwait(false);
        return vectors.Length > 0
            ? vectors[0]
            : throw new EmbeddingException(EmbeddingFailureKind.InvalidResponse,
                "Provider returned no vector for a single query input.");
    }

    public Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) return Task.FromResult(Array.Empty<float[]>());
        if (profile.DocumentPrefix.Length == 0) return client.EmbedAsync(texts, ct);
        return client.EmbedAsync(texts.Select(t => profile.DocumentPrefix + t).ToArray(), ct);
    }

    public async Task<EmbeddingProbe> ProbeAsync(CancellationToken ct = default)
    {
        // A real embed is the only signal that "reachable" also means
        // "ready": Ollama answers /api/tags with 200 while the model can't
        // load, and a hosted provider can accept auth yet refuse the model.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);
        try
        {
            var vectors = await client.EmbedAsync(["mailvec readiness probe"], cts.Token).ConfigureAwait(false);
            return vectors is { Length: > 0 } && vectors[0].Length > 0
                ? new EmbeddingProbe(EmbeddingProbeStatus.Available, profile.WireModel, ModelListed: true)
                : new EmbeddingProbe(EmbeddingProbeStatus.InvalidResponse, "empty probe vector");
        }
        catch (EmbeddingException ex)
        {
            var status = ex.Kind switch
            {
                EmbeddingFailureKind.AuthOrConfig => EmbeddingProbeStatus.AuthFailed,
                EmbeddingFailureKind.ModelUnavailable => EmbeddingProbeStatus.ModelMissing,
                EmbeddingFailureKind.Backpressure => EmbeddingProbeStatus.Backpressure,
                EmbeddingFailureKind.InvalidResponse => EmbeddingProbeStatus.InvalidResponse,
                _ => EmbeddingProbeStatus.Unreachable,
            };
            return await RefineAsync(status, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return await RefineAsync(EmbeddingProbeStatus.Unreachable, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await RefineAsync(EmbeddingProbeStatus.Unreachable, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ollama-style refinement of a failed probe: one bounded /api/tags call
    /// distinguishes "server down" from "server up, model not pulled" —
    /// opposite remediations. Providers whose transport answers null (the
    /// interface default for hosted profiles) keep the unrefined status; a
    /// rate-limited probe must not be refined into a missing-model claim.
    /// </summary>
    private async Task<EmbeddingProbe> RefineAsync(EmbeddingProbeStatus status, CancellationToken ct)
    {
        bool? listed = status is EmbeddingProbeStatus.ModelMissing ? false : null;
        if (status is EmbeddingProbeStatus.Unreachable)
        {
            // The 2s cap (not the probe's 5s): this runs serially after a
            // failed embed, and /health's compose healthcheck times out at
            // 10s — a hang-accepting server must not eat 5s twice. Every
            // scenario where the listing answers does so well inside 2s; a
            // server too hung to list reads as null, same as no listing.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeRefineTimeout);
            try
            {
                listed = await client.IsModelAvailableAsync(cts.Token).ConfigureAwait(false);
                if (listed is false) status = EmbeddingProbeStatus.ModelMissing;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* keep Unreachable */ }
        }
        return new EmbeddingProbe(status, profile.WireModel, listed);
    }

    public Task<string?> GetModelArtifactDigestAsync(CancellationToken ct = default) =>
        client.GetModelArtifactDigestAsync(ct);
}
