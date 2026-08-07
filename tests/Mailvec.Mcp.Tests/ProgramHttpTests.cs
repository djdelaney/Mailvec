using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task Up_endpoint_mirrors_health_status_code()
    {
        // The parity is the invariant, not the literal 503: monitors alert on
        // the status code, so a /up that always returned 200 would read healthy
        // forever while the archive was degraded — silently, since nothing
        // logs a successful probe. Asserting parity rather than a fixed code
        // keeps this meaningful if a future fixture stubs Ollama for an "ok"
        // path, and fails the moment the two endpoints' logic diverges.
        using var client = _factory.CreateClient();

        var up = await client.GetAsync("/up");
        var health = await client.GetAsync("/health");

        up.StatusCode.ShouldBe(health.StatusCode);
    }

    [Fact]
    public async Task Up_endpoint_body_discloses_only_status_and_version()
    {
        // /up exists to be polled by an internet-facing monitor whose
        // credential is the likeliest of ours to leak, so the point of the
        // endpoint IS the absence of these fields. Nothing else fails if
        // someone "helpfully" enriches the body, which is why this is pinned.
        using var client = _factory.CreateClient();

        var body = await (await client.GetAsync("/up")).Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);

        // An exact allowlist, not a count: the rule for this body is "booleans
        // yes, values no", and a new top-level block should have to be argued
        // for here rather than arriving with a feature.
        // `ocr` was argued for and admitted: a single boolean (`stalled`),
        // matching this body's "booleans yes, values no" rule. It carries no
        // last-success timestamp — deliberately, for the same reason
        // mail.syncStale withholds one, since a time polled every minute builds
        // a log of when the user's mail is active — and no page, pending or
        // retired counts, which would disclose corpus activity and spend.
        // It exists so a monitor can alert on OCR silently ceasing to produce
        // text, which no other field on this endpoint can express.
        doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n)
            .ShouldBe(["embedder", "embeddings", "mail", "ocr", "ollama", "services", "status", "version"]);

        // Belt and braces on the specific disclosures that motivated the split
        // — a renamed field would slip past a property-name check.
        body.ShouldNotContain("baseUrl");        // internal Ollama LAN address
        body.ShouldNotContain(".sqlite");        // archive filesystem path
        body.ShouldNotContain("messagesTotal");  // corpus size
        body.ShouldNotContain("schemaModel");    // embedding model identity
        body.ShouldNotContain("configModel");    // ditto
        body.ShouldNotContain("chunkCount");     // corpus size
        body.ShouldNotContain("lastFailureKind"); // embedder error detail
        body.ShouldNotContain("lastBeatAt");     // per-service timestamps
        body.ShouldNotContain("lastSuccessAt");  // OCR activity timing (see mail.syncStale)
        body.ShouldNotContain("pagesSent");      // OCR volume / spend
        body.ShouldNotContain("retired");        // count of documents given up on
        body.ShouldNotContain("lastSyncAt");     // when the user's mail arrives
    }

    /// <summary>
    /// The six Uptime Kuma monitors evaluate JSONata against this body. A
    /// renamed or re-nested field doesn't fail anything — JSONata resolves the
    /// missing path to nothing and the monitor silently stops being able to
    /// match its expected value. These are the exact paths from
    /// docs/monitoring-uptime-kuma.md; keep them identical to /health's so one
    /// query works against either endpoint.
    /// </summary>
    [Fact]
    public async Task Up_body_carries_every_field_the_uptime_monitors_query()
    {
        using var client = _factory.CreateClient();
        var doc = JsonDocument.Parse(await (await client.GetAsync("/up")).Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // ollama.reachable / embedder.stuck / embeddings.modelMismatch
        root.GetProperty("ollama").GetProperty("reachable").ValueKind
            .ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
        root.GetProperty("embedder").GetProperty("stuck").ValueKind
            .ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
        root.GetProperty("embeddings").GetProperty("modelMismatch").ValueKind
            .ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);

        // mail.syncStale — the sidecar beats whether or not its syncs work, so
        // this is the only signal that distinguishes a healthy sidecar from one
        // failing every pull. Nothing else in the pipeline can tell.
        root.GetProperty("mail").GetProperty("syncStale").ValueKind
            .ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
        root.GetProperty("mail").GetProperty("known").ValueKind
            .ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);

        // services[service='indexer'|'embedder'|'mbsync'].stale
        var services = root.GetProperty("services").EnumerateArray().ToList();
        services.Select(s => s.GetProperty("service").GetString())
            .ShouldBe(["indexer", "embedder", "mbsync"], ignoreOrder: true);
        foreach (var s in services)
        {
            s.GetProperty("stale").ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
            s.GetProperty("known").ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
        }

        // The single-monitor fallback query in the same doc is
        // "status = 'ok' and $count(services[stale = true]) = 0
        //  and mail.syncStale = false".
        root.GetProperty("status").GetString().ShouldNotBeNullOrEmpty();
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

    // ---------- /health is loopback-only ----------
    //
    // /health discloses the archive's filesystem path, corpus counts, the
    // embedding model identity and the internal Ollama LAN address. /up exists
    // so that no external caller ever needs any of it, which only holds if
    // /health isn't reachable from off-box. Every documented consumer is
    // already loopback (the compose healthcheck and `mailvec doctor`), so
    // the restriction costs nothing — see McpOptions.RestrictHealthToLoopback.

    [Fact]
    public async Task Health_is_not_served_to_an_off_box_caller()
    {
        using var factory = new RemoteCallerFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        // 404, not 403: a refusal confirms the endpoint is there, and no caller
        // benefits from learning that.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Health_refusal_discloses_nothing_from_the_report()
    {
        // The disclosure is the whole point of the restriction, so pin that a
        // refusal body can't carry it — a future "helpful" 404 payload naming
        // the archive path would defeat the control while still being a 404.
        using var factory = new RemoteCallerFactory();
        using var client = factory.CreateClient();

        var body = await (await client.GetAsync("/health")).Content.ReadAsStringAsync();

        body.ShouldNotContain("baseUrl");
        body.ShouldNotContain(".sqlite");
        body.ShouldNotContain("messagesTotal");
    }

    [Fact]
    public async Task Up_is_still_served_to_an_off_box_caller()
    {
        // The other half: /up is the endpoint that IS meant to be polled from
        // outside, so restricting /health must not have caught it. Uptime Kuma
        // polling through the tunnel depends on this.
        using var factory = new RemoteCallerFactory();
        using var client = factory.CreateClient();

        (await client.GetAsync("/up")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Health_restriction_can_be_turned_off()
    {
        using var factory = new RemoteCallerFactory(restrictHealth: false);
        using var client = factory.CreateClient();

        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
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
        // container to what a monitor can see.
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

    [Fact]
    public async Task Mcp_endpoint_serves_tools_list_with_no_handshake_and_no_session()
    {
        // The MCP SDK is stateless by default as of 2.0 (the 2026-07-28 protocol
        // revision) and we take that default — see CLAUDE.md "MCP transport
        // quirks". Three things now depend on it being true: the one-shot curl
        // recipes in docs/clients/claude-code.md and ops/UPGRADING.md, and
        // docs/deploy-docker.md's claim that the tunnel needs no session
        // affinity. Setting Stateless = false (the tempting move if someone
        // wants server-initiated elicitation back) breaks all three at once and
        // would otherwise only show up as a client-side 404 in production.
        using var client = _factory.CreateClient();

        var response = await client.SendAsync(JsonRpc("tools/list"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("Mcp-Session-Id").ShouldBeFalse(
            "stateless mode must not issue a session header");

        var tools = ResultOf(await response.Content.ReadAsStringAsync())
            .GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        // The locked surface (CLAUDE.md "MCP API stability"), reachable without
        // an initialize handshake.
        tools.ShouldContain("search_emails");
        tools.Count.ShouldBe(ToolSurface.All.Count);
    }

    /// <summary>
    /// A bare JSON-RPC POST to the MCP endpoint. Streamable HTTP requires the
    /// client to accept BOTH content types — omit either and the SDK answers
    /// 406 before the request reaches a tool, which is an easy way to
    /// misdiagnose a working server as broken when hand-rolling a curl probe.
    /// </summary>
    private static HttpRequestMessage JsonRpc(string method)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(
                $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\"}}",
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return request;
    }

    /// <summary>
    /// Unwraps the JSON-RPC result, tolerating the SDK's SSE framing
    /// ("event: message\ndata: {...}") as well as a plain JSON body.
    /// </summary>
    private static JsonElement ResultOf(string body)
    {
        const string dataPrefix = "data: ";
        var idx = body.IndexOf(dataPrefix, StringComparison.Ordinal);
        var json = idx >= 0 ? body[(idx + dataPrefix.Length)..] : body;
        return JsonDocument.Parse(json).RootElement.GetProperty("result");
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
                // Neutralize the developer's shared config. Every Mailvec binary
                // reads ~/Library/Application Support/Mailvec/appsettings.Local.json
                // (SharedConfig), so on a machine where the author has webmail
                // links configured, a test asserting their presence passes for
                // the wrong reason — and one asserting their absence fails only
                // on that machine and only for them. Tests that want links on
                // set the account id themselves.
                ["Fastmail:AccountId"] = "",
            });
        });

        // Declare this factory to be the loopback caller it stands in for — the
        // launchd/local install, where `mailvec doctor` and the compose
        // healthcheck both reach /health over 127.0.0.1. TestServer
        // leaves RemoteIpAddress null, and the /health loopback restriction
        // fails closed on null, so without this every /health test here 404s.
        // See RemoteIpStartupFilter.
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Loopback)));
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

/// <summary>
/// The same server seen by a caller that is NOT on loopback — what cloudflared
/// and every sibling container look like, since they connect to
/// <c>mcp:3333</c> over the compose network rather than to 127.0.0.1. This is
/// the vantage point the <c>/health</c> restriction exists for; from
/// <see cref="MailvecMcpFactory"/>'s loopback vantage point it is invisible.
/// </summary>
public sealed class RemoteCallerFactory : WebApplicationFactory<Program>
{
    private readonly string _tempDir;
    private readonly bool _restrictHealth;

    public RemoteCallerFactory(bool restrictHealth = true)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mailvec-remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _restrictHealth = restrictHealth;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Archive:DatabasePath"] = Path.Combine(_tempDir, "archive.sqlite"),
                ["Ollama:BaseUrl"] = "http://127.0.0.1:1",
                ["Ollama:RequestTimeoutSeconds"] = "5",
                ["Fastmail:AccountId"] = "",
                ["Mcp:RestrictHealthToLoopback"] = _restrictHealth ? "true" : "false",
            }));

        // A compose-network-looking address. The specific value doesn't matter;
        // "not loopback" does.
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Parse("172.18.0.7"))));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
