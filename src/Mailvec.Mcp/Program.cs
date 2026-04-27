using Mailvec.Core.Data;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Mailvec.Core.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

    // Re-route ALL log providers to stderr. The default console logger writes to
    // stdout, which would corrupt the MCP JSON-RPC stream.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    AddMailvecServices(builder.Services, builder.Configuration);

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly();

    var host = builder.Build();
    host.Services.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
    await host.RunAsync().ConfigureAwait(false);
}

static async Task RunHttp(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);

    AddMailvecServices(builder.Services, builder.Configuration);

    builder.Services
        .AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();

    var mcpOpts = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>() ?? new McpOptions();
    builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Parse(mcpOpts.BindAddress), mcpOpts.Port));

    var app = builder.Build();
    app.Services.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
    app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
    app.MapMcp();
    await app.RunAsync().ConfigureAwait(false);
}

static void AddMailvecServices(IServiceCollection services, IConfiguration config)
{
    services.Configure<ArchiveOptions>(config.GetSection(ArchiveOptions.SectionName));
    services.Configure<OllamaOptions>(config.GetSection(OllamaOptions.SectionName));
    services.Configure<McpOptions>(config.GetSection(McpOptions.SectionName));

    services.AddSingleton<ConnectionFactory>();
    services.AddSingleton<SchemaMigrator>();
    services.AddSingleton<MessageRepository>();
    services.AddSingleton<MetadataRepository>();
    services.AddSingleton<ChunkRepository>();
    services.AddSingleton<KeywordSearchService>();
    services.AddSingleton<VectorSearchService>();
    services.AddSingleton<HybridSearchService>();

    services.AddHttpClient<OllamaClient>((sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
        client.BaseAddress = new Uri(opts.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, opts.RequestTimeoutSeconds));
    });
}
