using System.Reflection;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Health;
using Mailvec.Core.Logging;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Mailvec.Core.Search;
using Mailvec.Core.Tray;
using Mailvec.Mcp.Tray;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

// Two transports share the same Core wiring:
//   --stdio  → Generic Host + StdioServerTransport (for Claude Desktop, since
//              its Custom Connectors require HTTPS that we can't provide locally).
//   default  → WebApplication + Streamable HTTP on Mcp:Port (for Claude Code,
//              future Tailscale-fronted Claude.ai, and our own smoke tests).
//
// Stdio mode MUST NOT log to stdout — that channel carries JSON-RPC frames.

if (args.Contains("--stdio", StringComparer.Ordinal))
{
    await RunStdio(args);
}
else
{
    await RunHttp(args);
}

static async Task RunStdio(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    // Stdio transport: stdout carries JSON-RPC frames, so SerilogSetup forces
    // the Console sink to stderr at all levels. The Serilog file sink is the
    // primary log; the stderr-console output is for Claude Desktop's
    // ~/Library/Logs/Claude/mcp-server-mailvec.log capture.
    SerilogSetup.Configure(builder.Services, builder.Configuration, builder.Logging, "mcp", stdioMode: true);

    AddMailvecServices(builder.Services, builder.Configuration);

    builder.Services
        .AddMcpServer(ConfigureServerInfo)
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    var host = builder.Build();
    host.Services.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
    await host.RunAsync().ConfigureAwait(false);
}

static async Task RunHttp(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);
    SerilogSetup.Configure(builder.Services, builder.Configuration, builder.Logging, "mcp");

    AddMailvecServices(builder.Services, builder.Configuration);

    builder.Services
        .AddMcpServer(ConfigureServerInfo)
        .WithHttpTransport()
        .WithToolsFromAssembly();

    var mcpOpts = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>() ?? new McpOptions();
    builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Parse(mcpOpts.BindAddress), mcpOpts.Port));

    var app = builder.Build();
    app.Services.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
    // /health returns a structured snapshot of DB / embedding / Ollama state.
    // Returns 503 when degraded so monitors can alert without parsing the body.
    app.MapGet("/health", async (HealthService health, CancellationToken ct) =>
    {
        var report = await health.CheckAsync(ct).ConfigureAwait(false);
        return report.Status == "ok"
            ? Results.Ok(report)
            : Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
    });
    // /tray/* serves the SwiftUI menu-bar app — plain REST, not MCP-framed.
    // See TrayEndpoints.cs.
    app.MapTrayEndpoints();
    app.MapMcp();
    await app.RunAsync().ConfigureAwait(false);
}

static void AddMailvecServices(IServiceCollection services, IConfiguration config)
{
    services.Configure<ArchiveOptions>(config.GetSection(ArchiveOptions.SectionName));
    services.Configure<IngestOptions>(config.GetSection(IngestOptions.SectionName));
    services.Configure<OllamaOptions>(config.GetSection(OllamaOptions.SectionName));
    services.Configure<McpOptions>(config.GetSection(McpOptions.SectionName));
    services.Configure<FastmailOptions>(config.GetSection(FastmailOptions.SectionName));

    services.AddSingleton<ConnectionFactory>();
    services.AddSingleton<SchemaMigrator>();
    services.AddSingleton<MessageRepository>();
    services.AddSingleton<MetadataRepository>();
    services.AddSingleton<ChunkRepository>();
    services.AddSingleton<KeywordSearchService>();
    services.AddSingleton<VectorSearchService>();
    services.AddSingleton<HybridSearchService>();
    services.AddSingleton<AttachmentExtractor>();
    services.AddSingleton<HealthService>();
    services.AddSingleton<Mailvec.Mcp.ToolCallLogger>();
    // Tray-facing services (consumed by the REST /tray/* endpoints).
    // TrayEventRecorder is a BackgroundService — it samples the DB once a
    // minute and keeps a 30-bucket ring buffer of embeddings/min.
    services.AddSingleton<LaunchdInspector>();
    services.AddSingleton<TrayEventRecorder>();
    services.AddHostedService(sp => sp.GetRequiredService<TrayEventRecorder>());
    services.AddSingleton<MbsyncErrorTail>();
    services.AddSingleton<TrayStatusService>();
    services.AddSingleton<TraySystemService>();
    services.AddSingleton<TraySearchService>();

    services.AddHttpClient<OllamaClient>((sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
        client.BaseAddress = new Uri(opts.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, opts.RequestTimeoutSeconds));
    });
}

// Surfaced to clients in the `initialize` response as `serverInfo`. The `name`
// is the protocol identifier (lowercase, stable — Phase 5 clients key off it
// in their config blocks); the `title` is the human-readable label some
// clients show in connector pickers; the `version` is read from the assembly
// (Mailvec.Mcp.csproj <Version>, kept in sync with manifest.json by
// ops/build-mcpb.sh --bump).
//
// Why this matters: once Gemini CLI / Codex CLI / ChatGPT desktop start
// pointing at this server (Phase 5), being able to call `initialize` and see
// "I'm talking to mailvec 0.1.15" is the cheapest possible diagnostic when a
// tool call returns something unexpected ("did the user upgrade? am I on the
// build that has the new field?"). Without this, the server name defaults to
// the assembly filename, which is uninformative.
static void ConfigureServerInfo(ModelContextProtocol.Server.McpServerOptions opts)
{
    var asmVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
    opts.ServerInfo = new Implementation
    {
        Name = "mailvec",
        Title = "Mailvec",
        Version = asmVersion,
    };
}

// Required for WebApplicationFactory<Program> in tests to discover the entry point.
public partial class Program;
