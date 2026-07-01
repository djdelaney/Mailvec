using Mailvec.Core.Data;
using Mailvec.Core.Health;
using Mailvec.Core.Options;
using Mailvec.Core.Tests.Data;
using Mailvec.Core.Tray;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mailvec.Core.Tests.Tray;

/// <summary>
/// Unit tests for the pure-logic bits of <see cref="TrayStatusService"/>
/// (service classification, severity rollup, embed-progress ETA) plus one
/// end-to-end <c>BuildAsync</c> integration against an empty database.
/// </summary>
public class TrayStatusServiceTests
{
    [Fact]
    public void ClassifyService_long_lived_running_is_ok()
    {
        var info = new LaunchdServiceInfo("com.mailvec.mcp", Loaded: true, State: "running", Pid: 1234, LastExitCode: 0, Runs: 1);
        var (ok, busy, severity, detail) = TrayStatusService.ClassifyService("mcp", info, HealthyReport(coverage: 100));
        ok.ShouldBeTrue();
        busy.ShouldBeFalse();
        severity.ShouldBe("ok");
        detail.ShouldBe("idle");
    }

    [Fact]
    public void ClassifyService_embedder_running_with_coverage_below_100_is_syncing()
    {
        var info = new LaunchdServiceInfo("com.mailvec.embedder", true, "running", 1234, 0, 1);
        var (ok, busy, severity, detail) = TrayStatusService.ClassifyService("embedder", info, HealthyReport(coverage: 60));
        ok.ShouldBeTrue();
        busy.ShouldBeTrue();
        severity.ShouldBe("syncing");
        detail.ShouldBe("embedding");
    }

    [Fact]
    public void ClassifyService_long_lived_not_running_is_error()
    {
        var info = new LaunchdServiceInfo("com.mailvec.mcp", true, "not running", null, 0, 1);
        var (ok, _, severity, detail) = TrayStatusService.ClassifyService("mcp", info, HealthyReport());
        ok.ShouldBeFalse();
        severity.ShouldBe("error");
        detail.ShouldBe("stopped");
    }

    [Fact]
    public void ClassifyService_long_lived_crashed_includes_exit_code()
    {
        var info = new LaunchdServiceInfo("com.mailvec.mcp", true, "not running", null, 137, 1);
        var (ok, _, severity, detail) = TrayStatusService.ClassifyService("mcp", info, HealthyReport());
        ok.ShouldBeFalse();
        severity.ShouldBe("error");
        detail.ShouldContain("137");
    }

    [Fact]
    public void ClassifyService_mbsync_idle_with_clean_exit_is_ok()
    {
        var info = new LaunchdServiceInfo("com.mailvec.mbsync", true, "not running", null, 0, 5);
        var (ok, busy, severity, detail) = TrayStatusService.ClassifyService("mbsync", info, HealthyReport());
        ok.ShouldBeTrue();
        busy.ShouldBeFalse();
        severity.ShouldBe("ok");
        detail.ShouldContain("idle");
        detail.ShouldContain("5");
    }

    [Fact]
    public void ClassifyService_mbsync_single_failed_run_is_warn_not_error()
    {
        // Single failed mbsync run is typically a lock collision that
        // self-recovers — we want "warn" (yellow tile), not "error" (red
        // banner). Worth a test because the design comment specifically
        // calls out this behaviour and a refactor could easily lose it.
        var info = new LaunchdServiceInfo("com.mailvec.mbsync", true, "not running", null, 1, 5);
        var (ok, _, severity, _) = TrayStatusService.ClassifyService("mbsync", info, HealthyReport());
        ok.ShouldBeFalse();
        severity.ShouldBe("warn");
    }

    [Fact]
    public void ClassifyService_not_loaded_returns_error_not_installed()
    {
        var info = new LaunchdServiceInfo("com.mailvec.mcp", Loaded: false, State: "unloaded", Pid: null, LastExitCode: null, Runs: 0);
        var (ok, _, severity, detail) = TrayStatusService.ClassifyService("mcp", info, HealthyReport());
        ok.ShouldBeFalse();
        severity.ShouldBe("error");
        detail.ShouldBe("not installed");
    }

    [Fact]
    public void ClassifyService_paused_returns_warn_not_error()
    {
        // The Pause button shells out to `launchctl bootout`, which leaves
        // the plist on disk but unloads the agent. LaunchdInspector flags
        // that case with State == "paused" so we don't fire the red
        // "indexer is in trouble — not installed" banner at a user who
        // just clicked Pause.
        var info = new LaunchdServiceInfo("com.mailvec.indexer", Loaded: false, State: "paused", Pid: null, LastExitCode: null, Runs: 0);
        var (ok, busy, severity, detail) = TrayStatusService.ClassifyService("indexer", info, HealthyReport());
        ok.ShouldBeFalse();
        busy.ShouldBeFalse();
        severity.ShouldBe("warn");
        detail.ShouldBe("paused");
    }

    [Theory]
    [InlineData(MbsyncErrorKind.Locked, "error")]
    [InlineData(MbsyncErrorKind.Dns, "warn")]
    [InlineData(MbsyncErrorKind.Network, "warn")]
    [InlineData(MbsyncErrorKind.Auth, "warn")]
    [InlineData(MbsyncErrorKind.Other, "warn")]
    public void ApplyMbsyncErrorOverride_escalates_only_locked_to_error(MbsyncErrorKind kind, string expectedSeverity)
    {
        // Locked is the one mbsync error class that doesn't self-recover —
        // every other kind is usually transient. The override mapping is
        // load-bearing for the dashboard's red-banner trigger.
        var err = new MbsyncError("Channel ‘chan’ is locked", kind, DateTimeOffset.UtcNow, 1200);
        var (ok, _, severity, _) = TrayStatusService.ApplyMbsyncErrorOverride(err);
        ok.ShouldBeFalse();
        severity.ShouldBe(expectedSeverity);
    }

    [Theory]
    [InlineData(+60, "ok")]    // new mail indexed 1 min AFTER the error → recovered
    [InlineData(-60, "error")] // last index 1 min BEFORE the error → still stuck
    public void BuildServices_mbsync_recovers_when_new_mail_indexed_after_error(int indexedOffsetSeconds, string expectedSeverity)
    {
        var errorAt = new DateTimeOffset(2026, 5, 17, 9, 0, 0, TimeSpan.Zero);
        var lastIndexed = errorAt.AddSeconds(indexedOffsetSeconds);

        var baseReport = HealthyReport();
        var report = baseReport with { Database = baseReport.Database with { LastIndexedAt = lastIndexed } };

        // mbsync idle with exit 0 (the healthy-looking state the stderr
        // override exists to correct), other agents running cleanly.
        var map = new Dictionary<string, LaunchdServiceInfo>
        {
            ["com.mailvec.mbsync"] = new("com.mailvec.mbsync", Loaded: true, State: "not running", Pid: null, LastExitCode: 0, Runs: 5),
            ["com.mailvec.indexer"] = new("com.mailvec.indexer", true, "running", 1, 0, 1),
            ["com.mailvec.embedder"] = new("com.mailvec.embedder", true, "running", 2, 0, 1),
            ["com.mailvec.mcp"] = new("com.mailvec.mcp", true, "running", 3, 0, 1),
        };
        var err = new MbsyncError("Error: channel is locked", MbsyncErrorKind.Locked, errorAt, 1200);

        var services = TrayStatusService.BuildServices(map, report, err);
        var mbsync = services.First(s => s.Id == "mbsync");
        mbsync.Severity.ShouldBe(expectedSeverity);
    }

    [Fact]
    public void BuildProgress_returns_null_when_coverage_complete()
    {
        var h = ReportWith(messagesTotal: 100, messagesDeleted: 0, embedded: 100);
        TrayStatusService.BuildProgress(h, ratePerMin: 5).ShouldBeNull();
    }

    [Fact]
    public void BuildProgress_returns_eta_in_minutes_when_rate_positive()
    {
        var h = ReportWith(messagesTotal: 100, messagesDeleted: 0, embedded: 40);
        var progress = TrayStatusService.BuildProgress(h, ratePerMin: 6).ShouldNotBeNull();
        progress.Done.ShouldBe(40);
        progress.Total.ShouldBe(100);
        // ceil(60 remaining / 6 per min) = 10
        progress.EtaMinutes.ShouldBe(10);
    }

    [Fact]
    public void BuildProgress_returns_zero_eta_when_rate_unknown()
    {
        var h = ReportWith(messagesTotal: 100, messagesDeleted: 0, embedded: 40);
        var progress = TrayStatusService.BuildProgress(h, ratePerMin: 0).ShouldNotBeNull();
        progress.EtaMinutes.ShouldBe(0);
    }

    [Fact]
    public void ClassifySeverity_returns_error_when_model_mismatch()
    {
        var h = WithMismatchedModel(ReportWith(100, 0, 100));
        var sev = TrayStatusService.ClassifySeverity(h,
            services: [Tile("ok")],
            ollama: new TrayOllamaStatus(true, "ok", "ok"),
            progress: null);
        sev.ShouldBe("error");
    }

    [Fact]
    public void ClassifySeverity_returns_error_when_ollama_unreachable()
    {
        var sev = TrayStatusService.ClassifySeverity(HealthyReport(),
            services: [Tile("ok")],
            ollama: new TrayOllamaStatus(false, "unreachable", "error"),
            progress: null);
        sev.ShouldBe("error");
    }

    [Fact]
    public void ClassifySeverity_returns_error_when_any_service_is_error()
    {
        var sev = TrayStatusService.ClassifySeverity(HealthyReport(),
            services: [Tile("ok"), Tile("error")],
            ollama: new TrayOllamaStatus(true, "ok", "ok"),
            progress: null);
        sev.ShouldBe("error");
    }

    [Fact]
    public void ClassifySeverity_returns_syncing_when_progress_in_flight()
    {
        var sev = TrayStatusService.ClassifySeverity(HealthyReport(),
            services: [Tile("ok")],
            ollama: new TrayOllamaStatus(true, "ok", "ok"),
            progress: new TrayEmbedProgress(50, 100, 10, 5));
        sev.ShouldBe("syncing");
    }

    [Fact]
    public void ClassifySeverity_returns_warn_when_a_service_is_paused_even_if_progress_in_flight()
    {
        // With the embedder paused, coverage stays below 100% so the
        // `progress is not null` branch would otherwise mark the system
        // "syncing" — a misleading label for an intentionally-stopped
        // pipeline. The paused-check must run before the progress check.
        var paused = new TrayServiceStatus("embedder", "paused", Ok: false, Busy: false, Severity: "warn");
        var sev = TrayStatusService.ClassifySeverity(HealthyReport(),
            services: [Tile("ok"), paused],
            ollama: new TrayOllamaStatus(true, "ok", "ok"),
            progress: new TrayEmbedProgress(50, 100, 0, 0));
        sev.ShouldBe("warn");
    }

    [Fact]
    public void ClassifySeverity_returns_ok_when_everything_clean()
    {
        var sev = TrayStatusService.ClassifySeverity(HealthyReport(),
            services: [Tile("ok"), Tile("ok")],
            ollama: new TrayOllamaStatus(true, "ok", "ok"),
            progress: null);
        sev.ShouldBe("ok");
    }

    [Fact]
    public async Task BuildAsync_returns_a_status_snapshot_against_empty_db()
    {
        using var db = new TempDatabase();
        var metadata = new MetadataRepository(db.Connections);
        metadata.Set("schema_version", "1");
        metadata.Set("embedding_model", "mxbai-embed-large");
        metadata.Set("embedding_dimensions", "1024");

        var http = new HttpClient(new UnreachableHandler())
        {
            BaseAddress = new Uri("http://localhost:11434"),
        };
        var ollamaOpts = Microsoft.Extensions.Options.Options.Create(new OllamaOptions
        {
            BaseUrl = "http://localhost:11434",
            EmbeddingDimensions = 1024,
            EmbeddingModel = "mxbai-embed-large",
        });
        var ollama = new Mailvec.Core.Ollama.OllamaClient(http, ollamaOpts, NullLogger<Mailvec.Core.Ollama.OllamaClient>.Instance);
        var archiveOpts = Microsoft.Extensions.Options.Options.Create(new ArchiveOptions { DatabasePath = db.DatabasePath });
        var health = new HealthService(db.Connections, metadata, ollama, archiveOpts, ollamaOpts);
        var launchd = new LaunchdInspector(NullLogger<LaunchdInspector>.Instance);
        var events = new TrayEventRecorder(db.Connections, NullLogger<TrayEventRecorder>.Instance);
        var mbsyncErrors = new MbsyncErrorTail();

        var service = new TrayStatusService(
            health, events, launchd, db.Connections, metadata, mbsyncErrors,
            archiveOpts, NullLogger<TrayStatusService>.Instance);

        var snapshot = await service.BuildAsync();

        // Ollama unreachable in tests → error severity dominates.
        snapshot.Severity.ShouldBe("error");
        snapshot.Messages.ShouldBe(0);
        snapshot.Embedded.ShouldBe(0);
        snapshot.SchemaVersion.ShouldBe("1");
        // Sparkline buffer is always 30 entries (ring buffer, even cold-start).
        snapshot.Sparkline.Count.ShouldBe(30);
        // Four service tiles: mbsync / indexer / embedder / mcp.
        snapshot.Services.Count.ShouldBe(4);
    }

    // ---------- helpers ----------

    private static HealthReport HealthyReport(long messagesTotal = 100, long messagesDeleted = 0, long embedded = 100, double coverage = 100)
    {
        return new HealthReport(
            Status: "ok",
            Database: new DatabaseHealth(
                Path: "/tmp/x.sqlite",
                MessagesTotal: messagesTotal,
                MessagesDeleted: messagesDeleted,
                LastIndexedAt: DateTimeOffset.UtcNow),
            Embeddings: new EmbeddingHealth(
                SchemaModel: "mxbai-embed-large",
                SchemaDimensions: 1024,
                ConfigModel: "mxbai-embed-large",
                ConfigDimensions: 1024,
                ModelMismatch: false,
                MessagesEmbedded: embedded,
                CoveragePct: coverage,
                ChunkCount: 0),
            Ollama: new OllamaHealth("http://localhost:11434", Reachable: true, ConfiguredModel: "mxbai-embed-large"),
            Embedder: new EmbedderHealth(
                LastSuccessAt: DateTimeOffset.UtcNow,
                LastFailureAt: null,
                ConsecutiveFailures: 0,
                LastFailureKind: null,
                Stuck: false),
            Ocr: new OcrHealth(
                Enabled: true,
                VisionModel: "qwen2.5vl:7b",
                ModelAvailable: true,
                Pending: 0,
                Recovered: 0,
                ImagePending: 0,
                ImageRecovered: 0));
    }

    private static HealthReport ReportWith(long messagesTotal, long messagesDeleted, long embedded)
    {
        var live = Math.Max(messagesTotal - messagesDeleted, 0);
        var coverage = live == 0 ? 0 : (double)embedded / live * 100d;
        return HealthyReport(messagesTotal, messagesDeleted, embedded, coverage);
    }

    private static HealthReport WithMismatchedModel(HealthReport r)
    {
        return r with
        {
            Embeddings = r.Embeddings with { ModelMismatch = true },
        };
    }

    private static TrayServiceStatus Tile(string severity) =>
        new("id", "detail", Ok: severity == "ok", Busy: false, Severity: severity);

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"));
    }
}
