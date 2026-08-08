using Mailvec.Core.Embedding;

namespace Mailvec.Core.Tests.Embedding;

/// <summary>
/// The purpose-aware service contract: transforms are applied exactly once,
/// to the right purpose, and the probe classifies rather than booleanizes.
/// </summary>
public class EmbeddingServiceTests
{
    [Fact]
    public async Task Query_transform_applies_to_queries_and_never_to_documents()
    {
        // The asymmetry IS the feature: instruction-tuned models are trained
        // with an instructed query against bare passages. A prefix leaking
        // onto documents (or vice versa) splits the vector space silently.
        var fake = new CapturingClient();
        var service = new EmbeddingService(fake, TestProfiles.Legacy(queryPrefix: "Q: ") with { OutputDimensions = 1 });

        await service.EmbedQueryAsync("find my flight");
        fake.LastInputs![0].ShouldBe("Q: find my flight");

        await service.EmbedDocumentsAsync(["chunk one", "chunk two"]);
        fake.LastInputs!.ShouldBe(["chunk one", "chunk two"]);
    }

    [Fact]
    public async Task All_four_transforms_are_applied_to_their_own_purpose_exactly_once()
    {
        var fake = new CapturingClient();
        var profile = TestProfiles.Legacy(queryPrefix: "q<") with
        {
            QuerySuffix = ">q",
            DocumentPrefix = "d<",
            DocumentSuffix = ">d",
            OutputDimensions = 1,
        };
        var service = new EmbeddingService(fake, profile);

        await service.EmbedQueryAsync("find it");
        fake.LastInputs![0].ShouldBe("q<find it>q");

        await service.EmbedDocumentsAsync(["chunk"]);
        fake.LastInputs![0].ShouldBe("d<chunk>d");
    }

    [Fact]
    public async Task Probe_maps_classified_failures_without_refining_non_transient_ones()
    {
        // Backpressure must stay Backpressure: refining a rate-limited probe
        // through the model listing could misreport a present model as the
        // problem. Only Unreachable earns the tags follow-up.
        var service = new EmbeddingService(
            new ThrowingClient(new EmbeddingException(EmbeddingFailureKind.Backpressure, "429")),
            TestProfiles.Legacy());

        var probe = await service.ProbeAsync();

        probe.Status.ShouldBe(EmbeddingProbeStatus.Backpressure);
        probe.IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task Probe_success_reports_available_with_model_listed()
    {
        var service = new EmbeddingService(new CapturingClient(), TestProfiles.Legacy());
        var probe = await service.ProbeAsync();
        probe.Status.ShouldBe(EmbeddingProbeStatus.Available);
        probe.ModelListed.ShouldBe(true);
    }

    [Fact]
    public async Task The_service_owns_the_mathematical_contract_for_every_transport()
    {
        // Moved up from OllamaClient at the transport-boundary extraction:
        // width, finiteness and normalization are checked ONCE here, so the
        // OpenAI-compatible transport cannot ship without them. Fireworks
        // returns norms ~65 (verified live) — normalization is load-bearing.
        var unnormalized = new RawClient([[2f, 0f]]);
        var service = new EmbeddingService(unnormalized, TestProfiles.Legacy() with { OutputDimensions = 2 });
        (await service.EmbedDocumentsAsync(["x"]))[0].ShouldBe(new[] { 1f, 0f });

        // Already-normalized vectors pass through bit-for-bit (re-embeds must
        // stay byte-identical to stored vectors).
        var normalized = new RawClient([[0.6f, 0.8f]]);
        (await new EmbeddingService(normalized, TestProfiles.Legacy() with { OutputDimensions = 2 })
            .EmbedDocumentsAsync(["x"]))[0].ShouldBe(new[] { 0.6f, 0.8f });

        // Wrong width, wrong count, and non-finite values each refuse as
        // InvalidResponse before anything could reach sqlite-vec.
        foreach (var (client, dims) in new[]
        {
            (new RawClient([[1f, 0f, 0f]]), 2),           // 3-wide vs 2
            (new RawClient([[1f, 0f], [0f, 1f]]), 2),     // 2 vectors for 1 input
            (new RawClient([[float.NaN, 0f]]), 2),        // non-finite
        })
        {
            var ex = await Should.ThrowAsync<EmbeddingException>(
                () => new EmbeddingService(client, TestProfiles.Legacy() with { OutputDimensions = dims })
                    .EmbedDocumentsAsync(["x"]));
            ex.Kind.ShouldBe(EmbeddingFailureKind.InvalidResponse);
        }
    }

    private sealed class RawClient(float[][] vectors) : IEmbeddingClient
    {
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) =>
            Task.FromResult(vectors);
        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool?> IsModelAvailableAsync(CancellationToken ct = default) => Task.FromResult<bool?>(true);
    }

    private sealed class CapturingClient : IEmbeddingClient
    {
        public IReadOnlyList<string>? LastInputs;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
        {
            LastInputs = inputs;
            return Task.FromResult(inputs.Select(_ => new[] { 1f }).ToArray());
        }

        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool?> IsModelAvailableAsync(CancellationToken ct = default) => Task.FromResult<bool?>(true);
    }

    private sealed class ThrowingClient(EmbeddingException ex) : IEmbeddingClient
    {
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) => throw ex;
        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool?> IsModelAvailableAsync(CancellationToken ct = default) => Task.FromResult<bool?>(true);
    }
}
