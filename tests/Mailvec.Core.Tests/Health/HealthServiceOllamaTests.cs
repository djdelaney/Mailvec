using Mailvec.Core.Embedding;
using Mailvec.Core.Data;
using Mailvec.Core.Health;
using Mailvec.Core.Options;
using Mailvec.Core.Tests.Data;

namespace Mailvec.Core.Tests.Health;

/// <summary>
/// The Ollama tri-state on <see cref="HealthService.CheckAsync"/>: a failed
/// embed ping is followed up with the /api/tags model probe so /health (and
/// doctor, which reads this field) can distinguish "server down"
/// from "server up but the embedding model was never pulled" — the two need
/// opposite remediation, and conflating them used to send fresh-install users
/// restarting a healthy Ollama.
/// </summary>
public class HealthServiceOllamaTests
{
    // The fakes stay IEmbeddingTransport (transport-level) and are wrapped in
    // the REAL EmbeddingService, so these tests exercise the production
    // probe classification + tags refinement, not a fake's re-implementation.
    private static HealthService Build(TempDatabase db, IEmbeddingTransport embedding) =>
        new(db.Connections,
            new MetadataRepository(db.Connections),
            new EmbeddingService(embedding, Tests.Embedding.TestProfiles.Legacy()),
            Tests.Embedding.TestProfiles.Legacy(),
            Microsoft.Extensions.Options.Options.Create(new ArchiveOptions { DatabasePath = db.DatabasePath }),
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions()));

    [Fact]
    public async Task Successful_ping_implies_model_available_without_probing()
    {
        using var db = new TempDatabase();
        var fake = new FakeEmbedding(ping: true, modelAvailable: null);

        var r = await Build(db, fake).CheckAsync();

        r.Ollama.Reachable.ShouldBeTrue();
        // A real embed succeeded, so the model necessarily works — and the
        // extra /api/tags round-trip must be skipped — /health runs under the
        // compose healthcheck's 10s timeout.
        r.Ollama.EmbeddingModelAvailable.ShouldBe(true);
        fake.ProbeCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Failed_ping_with_model_absent_reports_not_pulled()
    {
        using var db = new TempDatabase();

        var r = await Build(db, new FakeEmbedding(ping: false, modelAvailable: false)).CheckAsync();

        r.Ollama.Reachable.ShouldBeFalse();
        r.Ollama.EmbeddingModelAvailable.ShouldBe(false);
        r.Status.ShouldBe("degraded");
    }

    [Fact]
    public async Task Failed_ping_with_server_down_reports_null()
    {
        using var db = new TempDatabase();

        var r = await Build(db, new FakeEmbedding(ping: false, modelAvailable: null)).CheckAsync();

        r.Ollama.Reachable.ShouldBeFalse();
        r.Ollama.EmbeddingModelAvailable.ShouldBeNull();
        r.Status.ShouldBe("degraded");
    }

    [Fact]
    public async Task Failed_ping_with_model_pulled_reports_cant_load()
    {
        using var db = new TempDatabase();

        var r = await Build(db, new FakeEmbedding(ping: false, modelAvailable: true)).CheckAsync();

        r.Ollama.Reachable.ShouldBeFalse();
        r.Ollama.EmbeddingModelAvailable.ShouldBe(true);
    }

    [Fact]
    public async Task Hung_model_probe_is_deadline_capped_and_reads_as_unknown()
    {
        // A hang-accepting Ollama (host suspended mid-connection) eats the
        // ping's full 5s AND used to eat the follow-up probe's full 5s
        // serially — ~10s per /health, which is the compose healthcheck's own
        // timeout, so a slow Ollama made the container fail its healthcheck
        // rather than report one. The
        // follow-up now carries its own 2s deadline; a server too hung to
        // list tags reads as null, the same answer the full-length probe
        // gives. Without the cap this test never completes (the fake only
        // ends on the caller's token).
        using var db = new TempDatabase();

        var r = await Build(db, new HangingProbeEmbedding()).CheckAsync();

        r.Ollama.Reachable.ShouldBeFalse();
        r.Ollama.EmbeddingModelAvailable.ShouldBeNull();
        r.Status.ShouldBe("degraded");
    }

    [Fact]
    public async Task The_report_carries_the_resolved_profile_identity_without_secrets()
    {
        using var db = new TempDatabase();
        var r = await Build(db, new FakeEmbedding(ping: true, modelAvailable: null)).CheckAsync();

        r.Profile.ShouldNotBeNull();
        r.Profile!.Name.ShouldBe("ollama-legacy");
        r.Profile.Protocol.ShouldBe("ollama");
        r.Profile.ProviderId.ShouldBe("ollama");
        r.Profile.EndpointHost.ShouldBe("localhost");   // host only, never the full URL
        r.Profile.WireModel.ShouldBe("mxbai-embed-large");
        r.Profile.Dimensions.ShouldBe(1024);
        r.Profile.SpaceId.ShouldBe("ollama:mxbai-embed-large:1024");
        r.Profile.ProbeStatus.ShouldBe("Available");
    }

    [Fact]
    public async Task A_standing_sentinel_drift_marker_degrades_health()
    {
        // Reads are refused by the guard while the marker stands; a green
        // /health beside a down semantic search is the silent state the
        // widened flag exists to prevent.
        using var db = new TempDatabase();
        new MetadataRepository(db.Connections).Set(EmbeddingSpace.SentinelDriftKey, "2026-08-08T12:00:00Z");

        var r = await Build(db, new FakeEmbedding(ping: true, modelAvailable: null)).CheckAsync();

        r.Embeddings.ModelMismatch.ShouldBeTrue();
        r.Status.ShouldBe("degraded");
    }

    [Fact]
    public async Task Rapid_health_checks_coalesce_onto_one_real_probe()
    {
        // Every probe is a real embed — a paid request under a hosted
        // profile — and the healthcheck plus several /up monitors poll
        // continuously. The singleton caches for a short interval.
        using var db = new TempDatabase();
        var fake = new FakeEmbedding(ping: true, modelAvailable: null);
        var health = Build(db, fake);

        await health.CheckAsync();
        await health.CheckAsync();
        await health.CheckAsync();

        fake.EmbedCalls.ShouldBe(1);
    }

    private sealed class HangingProbeEmbedding : IEmbeddingTransport
    {
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) =>
            throw new EmbeddingException(EmbeddingFailureKind.Transient, "connection failed");


        public async Task<bool?> IsModelAvailableAsync(CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct); // hung server: only the caller's deadline ends this
            return null;
        }
    }

    private sealed class FakeEmbedding(bool ping, bool? modelAvailable) : IEmbeddingTransport
    {
        public int ProbeCalls;
        public int EmbedCalls;

        // ping=true -> the real embed succeeds; ping=false -> a classified
        // transport failure, which EmbeddingService refines via the tags
        // probe. Same tri-state the old PingAsync/IsModelAvailableAsync pair
        // expressed, now driven through the transport surface.
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
        {
            EmbedCalls++;
            if (!ping) throw new EmbeddingException(EmbeddingFailureKind.Transient, "connection failed");
            var v = new float[1024]; v[0] = 1f;   // probe validates full width now
            return Task.FromResult(new[] { v });
        }


        public Task<bool?> IsModelAvailableAsync(CancellationToken ct = default)
        {
            ProbeCalls++;
            return Task.FromResult(modelAvailable);
        }
    }
}
