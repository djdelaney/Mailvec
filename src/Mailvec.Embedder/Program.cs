using Mailvec.Core;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Logging;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Mailvec.Core.Vision;
using Mailvec.Embedder.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
// Single source of truth for DB / Ollama config. See SharedConfig.
builder.Configuration.AddMailvecSharedConfig();
SerilogSetup.Configure(builder.Services, builder.Configuration, builder.Logging, "embedder");

builder.Services.Configure<ArchiveOptions>(builder.Configuration.GetSection(ArchiveOptions.SectionName));
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));
builder.Services.Configure<EmbedderOptions>(builder.Configuration.GetSection(EmbedderOptions.SectionName));
// IngestOptions (MaildirRoot) — the embedder now reads .eml bytes for the
// scanned-PDF OCR pass. From the shared config, same as the indexer/MCP.
builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection(IngestOptions.SectionName));

builder.Services.AddSingleton<ConnectionFactory>();
builder.Services.AddSingleton<SchemaMigrator>();
builder.Services.AddSingleton<MessageRepository>();
builder.Services.AddSingleton<MetadataRepository>();
builder.Services.AddSingleton<ChunkRepository>();
builder.Services.AddSingleton<ChunkingService>();
builder.Services.AddSingleton<MaildirAttachmentReader>();

builder.Services
    .AddHttpClient<OllamaClient>((sp, client) =>
    {
        var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OllamaOptions>>().Value;
        client.BaseAddress = new Uri(opts.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(5, opts.RequestTimeoutSeconds));
    })
    .AddStandardResilienceHandler(o =>
    {
        // Embedding a batch can be slow on first model load; widen the per-attempt timeout.
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(120);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(300);
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(240);
    });
builder.Services.AddTransient<IEmbeddingClient>(sp => sp.GetRequiredService<OllamaClient>());

// Vision client for scanned-PDF OCR — its own HttpClient with a longer timeout
// (OCR runs much longer than an embed). No resilience handler: a transient
// failure just leaves the PDF for the next OCR pass.
builder.Services
    .AddHttpClient<OllamaVisionClient>((sp, client) =>
    {
        var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
        client.BaseAddress = new Uri(opts.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(Math.Max(30, opts.VisionRequestTimeoutSeconds));
    });
builder.Services.AddTransient<IVisionClient>(sp => sp.GetRequiredService<OllamaVisionClient>());
// AttachmentOcrService is native-renderer-backed and platform-gated; register it
// only where supported. On any other platform it stays unregistered and the
// EmbeddingWorker's optional OCR dependency falls back to null (OCR skipped).
if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<AttachmentOcrService>();
}

builder.Services.AddHostedService<EmbeddingWorker>();

var host = builder.Build();

// Log resolved DB path at startup so the operator can confirm which dataset
// (prod vs test) the service is running against. Embedder doesn't read the
// Maildir, so only the DB is relevant.
{
    var archive = host.Services.GetRequiredService<IOptions<ArchiveOptions>>().Value;
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Mailvec.Embedder");
    logger.LogInformation(
        "Embedder starting (database={DatabasePath})",
        PathExpansion.Expand(archive.DatabasePath));
}

host.Run();
