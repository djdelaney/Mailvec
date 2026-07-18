using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Mailvec.Mcp.Tests;

/// <summary>
/// Spins up the real ASP.NET Core MCP server in-process via WebApplicationFactory
/// to cover Program.cs (DI wiring, RunHttp, /health route mapping). The MCP route
/// itself is exercised through direct tool tests; this file only validates the
/// HTTP-only surface.
/// </summary>
public class ProgramHttpTests : IClassFixture<MailvecMcpFactory>
{
    private readonly MailvecMcpFactory _factory;

    public ProgramHttpTests(MailvecMcpFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_endpoint_returns_503_when_Ollama_unreachable()
    {
        // Tests don't run a real Ollama; HealthService.PingAsync fails →
        // status="degraded" → 503. This is the production failure mode worth
        // pinning since monitors page on it.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Health_endpoint_returns_structured_json_body()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        var body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);

        // Whatever the status, the response carries the report's top-level fields.
        // Don't pin status="degraded" only — leaves room for the test to remain
        // useful if a future fixture stubs Ollama for an "ok" path.
        doc.RootElement.TryGetProperty("status", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Rejects_request_with_foreign_host_header()
    {
        // DNS-rebinding guard: a browser rebound to 127.0.0.1 still sends the
        // attacker's hostname in Host. Must be refused before reaching /health.
        using var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Host = "evil.com";

        var response = await client.SendAsync(req);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Rejects_request_with_foreign_origin_header()
    {
        using var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        // Host defaults to localhost (allowed); Origin reveals the cross-site caller.
        req.Headers.Add("Origin", "http://evil.com");

        var response = await client.SendAsync(req);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Allows_loopback_request_through_the_guard()
    {
        // Default WebApplicationFactory client sends Host: localhost and no
        // Origin — the guard must let it through (503 here only because Ollama
        // is unreachable in tests, not 403).
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Health_endpoint_reports_the_binary_version()
    {
        // Deploy verification leans on this: after pinning a v* image tag,
        // one /health call must say which version is actually serving
        // (docs/deploy-docker.md "Deploying it"). Pins presence and the
        // three-part shape, and that it matches the stamped assembly version.
        using var client = _factory.CreateClient();

        var doc = JsonDocument.Parse(await (await client.GetAsync("/health")).Content.ReadAsStringAsync());

        doc.RootElement.TryGetProperty("version", out var version).ShouldBeTrue();
        var value = version.GetString();
        value.ShouldNotBeNull();
        value.ShouldMatch(@"^\d+\.\d+\.\d+$");
        value.ShouldBe(typeof(Mailvec.Core.Health.HealthService).Assembly.GetName().Version?.ToString(3));
    }

    [Fact]
    public async Task Health_endpoint_carries_per_service_liveness()
    {
        // The /health body must expose the indexer/embedder/mbsync liveness the
        // container deployment relies on (no launchctl to ask). This pins the
        // wire shape: an array of {service, known, stale, ...}. It deliberately
        // does NOT assert the known/stale *values* — the factory DB is shared
        // across this class's tests (IClassFixture) and another test writes a
        // beat, so a value assertion here would be order-dependent. The
        // fresh-DB-is-unknown and beat-flips-to-live behaviours are pinned in
        // Core's ServiceHeartbeat tests and in the beat test below respectively.
        using var client = _factory.CreateClient();

        var doc = JsonDocument.Parse(await (await client.GetAsync("/health")).Content.ReadAsStringAsync());

        doc.RootElement.TryGetProperty("services", out var services).ShouldBeTrue();
        services.ValueKind.ShouldBe(JsonValueKind.Array);

        var names = services.EnumerateArray().Select(s => s.GetProperty("service").GetString()).ToList();
        names.ShouldContain("indexer");
        names.ShouldContain("embedder");
        names.ShouldContain("mbsync");

        foreach (var svc in services.EnumerateArray())
        {
            // Both flags present and boolean on every entry, whatever their value.
            svc.GetProperty("known").ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
            svc.GetProperty("stale").ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
        }
    }

    [Fact]
    public async Task Health_endpoint_reflects_a_live_beat()
    {
        // End-to-end through the real endpoint: write a beat the way a worker's
        // HeartbeatService would, then confirm /health flips that service to
        // known+live. This is the wire that connects a running background
        // container to what a monitor or the tray can see.
        using var client = _factory.CreateClient();
        var metadata = new Mailvec.Core.Data.MetadataRepository(
            new Mailvec.Core.Data.ConnectionFactory(
                Microsoft.Extensions.Options.Options.Create(
                    new Mailvec.Core.Options.ArchiveOptions { DatabasePath = _factory.DatabasePath })));

        Mailvec.Core.Health.ServiceHeartbeat.Beat(
            metadata, Mailvec.Core.Health.ServiceHeartbeat.Indexer, TimeSpan.FromSeconds(60));

        var doc = JsonDocument.Parse(await (await client.GetAsync("/health")).Content.ReadAsStringAsync());

        var indexer = doc.RootElement.GetProperty("services").EnumerateArray()
            .Single(s => s.GetProperty("service").GetString() == "indexer");
        indexer.GetProperty("known").GetBoolean().ShouldBeTrue();
        indexer.GetProperty("stale").GetBoolean().ShouldBeFalse();
        indexer.GetProperty("lastBeatAt").ValueKind.ShouldBe(JsonValueKind.String);
        indexer.GetProperty("expectedIntervalSeconds").GetInt32().ShouldBe(60);
    }

    [Fact]
    public async Task Health_endpoint_responds_quickly_thanks_to_ping_timeout()
    {
        // CLAUDE.md gotcha: the OllamaClient.PingAsync wraps the call in a 2s
        // linked CTS so /health doesn't hang for the embedder's 60s timeout.
        // Worst case here is a few seconds; pad generously to avoid flakes.
        using var client = _factory.CreateClient();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await client.GetAsync("/health");
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }
}

public class TrayEndpointsHttpTests : IClassFixture<MailvecMcpFactory>
{
    private readonly MailvecMcpFactory _factory;

    public TrayEndpointsHttpTests(MailvecMcpFactory factory) => _factory = factory;

    private Mailvec.Core.Data.MessageRepository Repo() => new(
        new Mailvec.Core.Data.ConnectionFactory(
            Microsoft.Extensions.Options.Options.Create(
                new Mailvec.Core.Options.ArchiveOptions { DatabasePath = _factory.DatabasePath })));

    private static Mailvec.Core.Parsing.ParsedMessage Sample(string id) => new(
        MessageId: id, ThreadId: id, Subject: id, FromAddress: "a@x", FromName: null,
        ToAddresses: [], CcAddresses: [], DateSent: DateTimeOffset.UtcNow, BodyText: "body",
        BodyHtml: null, RawHeaders: $"Message-ID: <{id}>\r\n", SizeBytes: 10,
        ContentHash: $"h-{id}", Attachments: []);

    [Fact]
    public async Task Tray_email_returns_404_for_soft_deleted_message()
    {
        // Soft-deleted = gone from disk. Every MCP tool refuses these; the
        // tray preview endpoint must too (a stale popover id used to get the
        // full body back).
        // Ensure the server (and its startup migration) ran before touching the DB.
        using var client = _factory.CreateClient();
        var repo = Repo();
        var id = repo.Upsert(Sample("traydel@x"), "INBOX", "INBOX/cur", "td", DateTimeOffset.UtcNow).Id;
        repo.MarkDeleted([id], DateTimeOffset.UtcNow);

        var response = await client.GetAsync($"/tray/email/{id}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Tray_email_returns_live_message()
    {
        using var client = _factory.CreateClient();
        var repo = Repo();
        var id = repo.Upsert(Sample("traylive@x"), "INBOX", "INBOX/cur", "tl", DateTimeOffset.UtcNow).Id;

        var response = await client.GetAsync($"/tray/email/{id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("traylive@x");
    }

    [Fact]
    public async Task Tray_control_with_missing_fields_is_a_400_not_a_500()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync("/tray/control",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}

/// <summary>
/// Covers the internet-fronted posture: Mcp:EnableTrayEndpoints=false (baked
/// into the container image) must remove the mail-bearing /tray/* surface at
/// the origin — defense-in-depth behind the tunnel's path-404 — while leaving
/// /health mapped for monitoring.
/// </summary>
public class TrayDisabledHttpTests : IClassFixture<TrayDisabledMcpFactory>
{
    private readonly TrayDisabledMcpFactory _factory;

    public TrayDisabledHttpTests(TrayDisabledMcpFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/tray/folders")]
    [InlineData("/tray/status")]
    [InlineData("/tray/email/1")]
    [InlineData("/tray/system")]
    public async Task Tray_endpoints_are_unmapped_when_disabled(string path)
    {
        // 404 = no route. The mail-bearing endpoints (/tray/email, /tray/folders,
        // /tray/search, /tray/system) must not exist at the origin at all — not
        // merely be gated — so a misconfigured tunnel rule can't reach them.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Health_stays_mapped_when_tray_is_disabled()
    {
        // /health is mapped independently of the tray surface — disabling tray
        // must not take monitoring down with it. 503 here (Ollama unreachable in
        // tests), never 404.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.ShouldNotBe(HttpStatusCode.NotFound);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.TryGetProperty("status", out _).ShouldBeTrue();
    }
}

public sealed class TrayDisabledMcpFactory : MailvecMcpFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mcp:EnableTrayEndpoints"] = "false",
            }));
    }
}

public class MailvecMcpFactory : WebApplicationFactory<Program>, IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public string DatabasePath => _dbPath;

    public MailvecMcpFactory()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mailvec-mcp-factory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "archive.sqlite");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Per-test fresh DB; SchemaMigrator runs at startup and creates it.
                ["Archive:DatabasePath"] = _dbPath,
                // Point Ollama at a port nothing's listening on — we want the
                // /health "Ollama unreachable" path covered without a real server.
                ["Ollama:BaseUrl"] = "http://127.0.0.1:1",
                // Cap embedder timeout so a stuck call never costs more than the
                // health endpoint's own 2s ping bound.
                ["Ollama:RequestTimeoutSeconds"] = "5",
            });
        });
    }

    public new void Dispose()
    {
        base.Dispose();
        // Scope the pool clear to THIS database (see TempDatabase) — a global
        // ClearAllPools() races with parallel test classes' in-use connections.
        // The pool key derives solely from DatabasePath, so a fresh
        // ConnectionFactory on _dbPath produces the same connection string.
        var connections = new Mailvec.Core.Data.ConnectionFactory(
            Microsoft.Extensions.Options.Options.Create(
                new Mailvec.Core.Options.ArchiveOptions { DatabasePath = _dbPath }));
        using (var conn = connections.Open())
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(conn);
        }
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
