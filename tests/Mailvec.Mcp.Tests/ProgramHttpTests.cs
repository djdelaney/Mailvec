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

public sealed class MailvecMcpFactory : WebApplicationFactory<Program>, IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
