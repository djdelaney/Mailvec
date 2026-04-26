using Mailvec.Core.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ArchiveOptions>(builder.Configuration.GetSection(ArchiveOptions.SectionName));
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));
builder.Services.Configure<McpOptions>(builder.Configuration.GetSection(McpOptions.SectionName));

// TODO Phase 3: AddMcpServer().WithHttpTransport().WithToolsFromAssembly()
// TODO Phase 3: bind Kestrel to Mcp:BindAddress / Mcp:Port from configuration

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
