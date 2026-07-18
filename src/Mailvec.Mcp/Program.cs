using System.Reflection;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Health;
using Mailvec.Core.Logging;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Mailvec.Core.Search;
using Mailvec.Core.Tray;
using Mailvec.Mcp;
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

    // Single source of truth shared with the launchd-installed services and
    // the CLI. See SharedConfig. Inserted before env vars so MCPB's
    // Mcp__LogToolCalls (passed in by Claude Desktop's user_config UI)
    // still wins.
    builder.Configuration.AddMailvecSharedConfig();

    // Stdio transport: stdout carries JSON-RPC frames, so SerilogSetup forces
    // the Console sink to stderr at all levels. The Serilog file sink is the
    // primary log; the stderr-console output is for Claude Desktop's
    // ~/Library/Logs/Claude/mcp-server-mailvec.log capture.
    SerilogSetup.Configure(builder.Services, builder.Configuration, builder.Logging, "mcp", stdioMode: true);

    AddMailvecServices(builder.Services, builder.Configuration);

    builder.Services
        .AddMcpServer(ConfigureServerInfo)
        .WithStdioServerTransport()
        .WithTools(EnabledTools(builder.Configuration));

    var host = builder.Build();
    WarnIfInstallerNeverRan(host.Services);
    host.Services.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
    await host.RunAsync().ConfigureAwait(false);
}

static async Task RunHttp(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);
    // Same shared file the stdio path and the other services read from.
    builder.Configuration.AddMailvecSharedConfig();
    SerilogSetup.Configure(builder.Services, builder.Configuration, builder.Logging, "mcp");

    AddMailvecServices(builder.Services, builder.Configuration);

    builder.Services
        .AddMcpServer(ConfigureServerInfo)
        .WithHttpTransport()
        .WithTools(EnabledTools(builder.Configuration));

    var mcpOpts = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>() ?? new McpOptions();
    // TryParse + a named error: Mcp:BindAddress takes an IP literal, and the
    // natural-looking value "localhost" used to crash with a bare
    // FormatException pointing nowhere near the config knob.
    if (!System.Net.IPAddress.TryParse(mcpOpts.BindAddress, out var bindAddress))
    {
        throw new InvalidOperationException(
            $"Mcp:BindAddress '{mcpOpts.BindAddress}' is not an IP address literal. " +
            "Use 127.0.0.1 (not \"localhost\") or another interface IP.");
    }
    builder.WebHost.ConfigureKestrel(k => k.Listen(bindAddress, mcpOpts.Port));

    var app = builder.Build();
    WarnIfInstallerNeverRan(app.Services);
    app.Services.GetRequiredService<SchemaMigrator>().EnsureUpToDate();

    // DNS-rebinding / same-origin guard. Runs before every route (MCP, /health,
    // /tray/*) so a browser rebound to 127.0.0.1 can't read mail or POST to the
    // mutating /tray endpoints. Loopback Host names are always allowed; add a
    // fronting hostname via Mcp:AllowedHosts. See HostGuard.
    var allowedHosts = HostGuard.BuildAllowedHosts(mcpOpts.AllowedHosts);
    app.Use(async (context, next) =>
    {
        if (!HostGuard.IsAllowed(context.Request.Host.Host, context.Request.Headers["Origin"].ToString(), allowedHosts))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next().ConfigureAwait(false);
    });

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
    // Gated off on internet-fronted deployments (Mcp:EnableTrayEndpoints=false,
    // baked into the container image): the surface is unauthenticated at the
    // origin and returns mail content, and nothing consumes it in a container.
    // Origin-side disable is defense-in-depth behind the tunnel's path-404 —
    // it holds even if that ingress rule is ever misconfigured. See
    // TrayEndpoints.cs and docs/security.md. /health above is unaffected.
    // Read from the bound options (post-Build), NOT the builder-time mcpOpts:
    // it's the DI-registered value, so an env var / appsettings override — and
    // the container image's baked Mcp__EnableTrayEndpoints=false — is reflected
    // here. (The builder-time mcpOpts is only used for Kestrel/middleware wiring
    // that has to happen before Build.)
    var trayEnabled = app.Services.GetRequiredService<IOptions<McpOptions>>().Value.EnableTrayEndpoints;
    if (trayEnabled)
    {
        app.MapTrayEndpoints();
    }
    else
    {
        app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Mailvec.Mcp.Startup")
            .LogInformation("Tray endpoints (/tray/*) disabled by Mcp:EnableTrayEndpoints=false.");
    }
    app.MapMcp();
    await app.RunAsync().ConfigureAwait(false);
}

// The MCPB bundle looks standalone but isn't: on a machine that never ran
// ops/install.sh there is no shared config, the default Archive:DatabasePath
// resolves to a path with nothing at it, and EnsureUpToDate (next line at both
// call sites) creates a fresh EMPTY database there — every search then returns
// zero results with no error anywhere. Deliberately a warning, not a refusal:
// in stdio mode a startup failure just means "the connector never appears"
// with the only clue buried in Claude Desktop's log — strictly worse. The
// per-call SetupHints on search_emails/list_folders are what make the state
// visible to the client LLM; this log line is for the human reading logs.
static void WarnIfInstallerNeverRan(IServiceProvider sp)
{
    var archive = sp.GetRequiredService<IOptions<ArchiveOptions>>().Value;
    var dbPath = Mailvec.Core.PathExpansion.Expand(archive.DatabasePath);
    if (!File.Exists(dbPath) && !SharedConfig.SharedConfigFileExists())
    {
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Mailvec.Mcp.Startup").LogWarning(
            "No database at {DbPath} and no shared config at {ConfigPath} — ops/install.sh has likely " +
            "never run on this machine. A fresh empty database will be created and every search will " +
            "return zero results until the installer runs and the indexer populates the archive.",
            dbPath, Mailvec.Core.PathExpansion.Expand(SharedConfig.SharedConfigPath));
    }
}

static void AddMailvecServices(IServiceCollection services, IConfiguration config)
{
    services.Configure<ArchiveOptions>(config.GetSection(ArchiveOptions.SectionName));
    services.Configure<IngestOptions>(config.GetSection(IngestOptions.SectionName));
    services.Configure<OllamaOptions>(config.GetSection(OllamaOptions.SectionName));
    services.Configure<McpOptions>(config.GetSection(McpOptions.SectionName));
    services.Configure<FastmailOptions>(config.GetSection(FastmailOptions.SectionName));
    // EmbedderOptions so HealthService can report whether OCR is enabled (the
    // MCP doesn't run OCR; it just surfaces the config + backlog to the tray).
    services.Configure<EmbedderOptions>(config.GetSection(EmbedderOptions.SectionName));

    services.AddSingleton<ConnectionFactory>();
    services.AddSingleton<SchemaMigrator>();
    services.AddSingleton<MessageRepository>();
    services.AddSingleton<MetadataRepository>();
    services.AddSingleton<ChunkRepository>();
    services.AddSingleton<KeywordSearchService>();
    services.AddSingleton<VectorSearchService>();
    services.AddSingleton<HybridSearchService>();
    services.AddSingleton<AttachmentExtractor>();
    // Reads mbsync's liveness beat off the Maildir mount — the sidecar can't
    // write the metadata table the other workers beat into. See
    // ServiceHeartbeat for why the three services report differently.
    services.AddSingleton<MbsyncHeartbeatFile>();
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
    services.AddTransient<IEmbeddingClient>(sp => sp.GetRequiredService<OllamaClient>());

    // Vision client so HealthService can probe whether the OCR model is pulled
    // (surfaced as a tray warn, never a /health 503). Mirrors CliServices.
    services.AddHttpClient<OllamaVisionClient>((sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
        client.BaseAddress = new Uri(opts.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(30, opts.VisionRequestTimeoutSeconds));
    });
    services.AddTransient<Mailvec.Core.Vision.IVisionClient>(sp => sp.GetRequiredService<OllamaVisionClient>());
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
// The tool classes to register for this deployment: the locked seven-tool
// surface minus Mcp:DisabledTools. Reads config directly (registration runs
// at builder time, before the options pipeline exists). Throws on unknown
// names — see ToolSurface.Resolve.
static IEnumerable<Type> EnabledTools(IConfiguration config) =>
    ToolSurface.Resolve(config.GetSection($"{McpOptions.SectionName}:{nameof(McpOptions.DisabledTools)}").Get<string[]>());

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
