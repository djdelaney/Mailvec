using Mailvec.Core.Embedding;
using Mailvec.Core.Data;
using Mailvec.Core.Health;
using Mailvec.Core.Options;
using Mailvec.Core.Tests.Data;

namespace Mailvec.Core.Tests.Health;

/// <summary>
/// The Ollama tri-state on <see cref="HealthService.CheckAsync"/>: a failed
/// embed ping is followed up with the /api/tags model probe so /health (and
/// doctor / the tray, which read this field) can distinguish "server down"
/// from "server up but the embedding model was never pulled" — the two need
/// opposite remediation, and conflating them used to send fresh-install users
/// restarting a healthy Ollama.
/// </summary>
public class HealthServiceOllamaTests
{
    private static HealthService Build(TempDatabase db, IEmbeddingClient embedding) =>
        new(db.Connections,
            new MetadataRepository(db.Connections),
            embedding,
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
        // extra /api/tags round-trip must be skipped (this runs on the tray's
        // 5s poll cadence).
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
        // serially — ~10s per /health while the tray polls every 5s. The
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

    private sealed class HangingProbeEmbedding : IEmbeddingClient
    {
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<float[]>());

        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(false);

        public async Task<bool?> IsModelAvailableAsync(CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct); // hung server: only the caller's deadline ends this
            return null;
        }
    }

    private sealed class FakeEmbedding(bool ping, bool? modelAvailable) : IEmbeddingClient
    {
        public int ProbeCalls;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<float[]>());

        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(ping);

        public Task<bool?> IsModelAvailableAsync(CancellationToken ct = default)
        {
            ProbeCalls++;
            return Task.FromResult(modelAvailable);
        }
    }
}
