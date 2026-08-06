using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Health;
using Mailvec.Core.Options;
using Mailvec.Core.Tests.Data;

namespace Mailvec.Core.Tests.Health;

/// <summary>
/// The mbsync sync-outcome block of <see cref="HealthService.CheckAsync"/>.
///
/// <para>The load-bearing assertion here is the NEGATIVE one: a broken mail
/// sync must not degrade <c>Status</c>. /health is the mcp container's own
/// compose healthcheck, and a sidecar that can't reach Fastmail says nothing
/// about whether MCP can serve search over everything already indexed —
/// folding it in would restart-loop a working container and point triage at
/// the wrong service. Same rule as <c>Services[].Stale</c>.</para>
/// </summary>
public class HealthServiceMailTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mailvec-mailhealth-" + Guid.NewGuid().ToString("N"));

    private MbsyncSyncFile SyncFile()
    {
        var maildir = Path.Combine(_root, "Fastmail");
        Directory.CreateDirectory(maildir);
        return new MbsyncSyncFile(
            Microsoft.Extensions.Options.Options.Create(new IngestOptions { MaildirRoot = maildir }));
    }

    private static HealthService Build(TempDatabase db, MbsyncSyncFile? sync) =>
        new(db.Connections,
            new MetadataRepository(db.Connections),
            new FakeEmbedding(),
            Microsoft.Extensions.Options.Options.Create(new ArchiveOptions { DatabasePath = db.DatabasePath }),
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions()),
            mbsyncSync: sync);

    [Fact]
    public async Task A_stale_sync_is_reported_but_never_degrades_status()
    {
        using var db = new TempDatabase();
        var sync = SyncFile();
        // Everything else healthy (FakeEmbedding pings true, no model mismatch,
        // empty backlog so the embedder can't be stuck) — so if mail were part
        // of the degraded set, this would be the one thing flipping it.
        File.WriteAllText(sync.Path!, $"{DateTimeOffset.UtcNow.AddHours(-6):yyyy-MM-ddTHH:mm:ssZ}\n600\n");

        var r = await Build(db, sync).CheckAsync();

        r.Mail.SyncStale.ShouldBeTrue();
        r.Mail.Known.ShouldBeTrue();
        r.Status.ShouldBe("ok");
    }

    [Fact]
    public async Task A_fresh_sync_reads_healthy_and_known()
    {
        using var db = new TempDatabase();
        var sync = SyncFile();
        File.WriteAllText(sync.Path!, $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\n600\n");

        var r = await Build(db, sync).CheckAsync();

        r.Mail.SyncStale.ShouldBeFalse();
        r.Mail.Known.ShouldBeTrue();
        r.Mail.ExpectedIntervalSeconds.ShouldBe(600);
    }

    [Fact]
    public async Task No_sync_file_reads_unknown_rather_than_broken()
    {
        // The launchd install and a fresh container both land here. Reporting
        // this as a failure would put every Mac dev install permanently red.
        using var db = new TempDatabase();

        var r = await Build(db, SyncFile()).CheckAsync();

        r.Mail.Known.ShouldBeFalse();
        r.Mail.SyncStale.ShouldBeFalse();
        r.Mail.LastSyncAt.ShouldBeNull();
    }

    [Fact]
    public async Task An_unwired_dependency_reads_unknown_rather_than_throwing()
    {
        // The dep is optional so the hand-built test/CLI graphs keep compiling.
        // Null must degrade to "no signal", matching the mbsync heartbeat.
        using var db = new TempDatabase();

        var r = await Build(db, sync: null).CheckAsync();

        r.Mail.Known.ShouldBeFalse();
        r.Mail.SyncStale.ShouldBeFalse();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class FakeEmbedding : IEmbeddingClient
    {
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<float[]>());
        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool?> IsModelAvailableAsync(CancellationToken ct = default) => Task.FromResult<bool?>(true);
    }
}
