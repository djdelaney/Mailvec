namespace Mailvec.Core.Embedding;

/// <summary>
/// The one implementation of <see cref="IEmbeddingService"/>: applies the
/// resolved profile's text policy, delegates the wire work to the
/// registered <see cref="IEmbeddingTransport"/> transport, and classifies
/// readiness. Constructed by <see cref="EmbeddingRegistration"/> with the
/// same resolved profile in every executable — the text policy applied to
/// queries here and to documents in the embedder is one object, not two
/// config reads.
/// </summary>
public sealed class EmbeddingService(IEmbeddingTransport client, ResolvedEmbeddingProfile profile) : IEmbeddingService
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
        // models. Only the query carries the instruction — that's how these
        // models are trained. Empty transforms (mxbai et al.) embed unchanged.
        var vectors = await client.EmbedAsync(
            [profile.QueryPrefix + text + profile.QuerySuffix], ct).ConfigureAwait(false);
        ValidateAndNormalize(vectors, expectedCount: 1);
        return vectors[0];
    }

    public async Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0) return [];
        var inputs = profile.DocumentPrefix.Length == 0 && profile.DocumentSuffix.Length == 0
            ? texts
            : texts.Select(t => profile.DocumentPrefix + t + profile.DocumentSuffix).ToArray();
        var vectors = await client.EmbedAsync(inputs, ct).ConfigureAwait(false);
        ValidateAndNormalize(vectors, texts.Count);
        return vectors;
    }

    /// <summary>
    /// Mathematical-contract enforcement, owned HERE per the proposal's
    /// service/transport boundary: transports serialize, classify, and return
    /// indexed raw vectors; the service — which holds the profile — checks
    /// count, dimension width, and finiteness ONCE for every provider, and
    /// L2-normalizes (vec0 KNN is L2; unit norm is what makes ranking
    /// cosine-equivalent). Verified live: Fireworks Qwen3 returns norms ~65,
    /// so this pass is load-bearing for hosted transports, not defensive.
    /// </summary>
    private void ValidateAndNormalize(float[][] vectors, int expectedCount)
    {
        if (vectors.Length != expectedCount)
            throw new EmbeddingException(EmbeddingFailureKind.InvalidResponse,
                $"Provider returned {vectors.Length} vectors for {expectedCount} inputs.");
        for (int i = 0; i < vectors.Length; i++)
        {
            var vec = vectors[i];
            if (vec.Length != profile.OutputDimensions)
                throw new EmbeddingException(EmbeddingFailureKind.InvalidResponse,
                    $"Vector {i} has {vec.Length} dimensions; profile '{profile.Name}' requires {profile.OutputDimensions}.");
            for (int j = 0; j < vec.Length; j++)
            {
                if (!float.IsFinite(vec[j]))
                    throw new EmbeddingException(EmbeddingFailureKind.InvalidResponse,
                        $"Vector {i} contains a non-finite value at index {j} — refusing to serialize it into sqlite-vec.");
            }
            VectorMath.NormalizeInPlaceIfNeeded(vec);
        }
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
            // The SAME mathematical contract as real embeddings — a probe
            // that only checks non-emptiness reports Available through a
            // provider-wide width/finiteness regression, and isolation mode
            // then reads that as "provider healthy" and quarantines valid
            // messages whose only crime was failing the way everything fails.
            ValidateAndNormalize(vectors, expectedCount: 1);
            return new EmbeddingProbe(EmbeddingProbeStatus.Available, profile.WireModel, ModelListed: true);
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
