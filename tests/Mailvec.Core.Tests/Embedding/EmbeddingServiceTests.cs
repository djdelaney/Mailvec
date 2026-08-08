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
        var service = new EmbeddingService(fake, TestProfiles.Legacy(queryPrefix: "Q: "));

        await service.EmbedQueryAsync("find my flight");
        fake.LastInputs![0].ShouldBe("Q: find my flight");

        await service.EmbedDocumentsAsync(["chunk one", "chunk two"]);
        fake.LastInputs!.ShouldBe(["chunk one", "chunk two"]);
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
