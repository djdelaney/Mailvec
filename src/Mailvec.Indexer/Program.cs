using Mailvec.Core;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Health;
using Mailvec.Core.Logging;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Indexer.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
// Single source of truth for DB / Maildir / Ollama config. Inserted
// before env vars so an ad-hoc `Ingest__MaildirRoot=...` override still
// works during development. See SharedConfig for the file path.
builder.Configuration.AddMailvecSharedConfig();
SerilogSetup.Configure(builder.Services, builder.Configuration, builder.Logging, "indexer");

builder.Services.Configure<ArchiveOptions>(builder.Configuration.GetSection(ArchiveOptions.SectionName));
builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection(IngestOptions.SectionName));
builder.Services.Configure<IndexerOptions>(builder.Configuration.GetSection(IndexerOptions.SectionName));

builder.Services.AddSingleton<ConnectionFactory>();
builder.Services.AddSingleton<SchemaMigrator>();
builder.Services.AddSingleton<MetadataRepository>();
builder.Services.AddSingleton<MessageRepository>();
builder.Services.AddSingleton<ChunkRepository>();
builder.Services.AddSingleton<SyncStateRepository>();
builder.Services.AddSingleton<AttachmentTextExtractor>();
builder.Services.AddSingleton<MessageParser>();
builder.Services.AddSingleton<MaildirScanner>();
builder.Services.AddSingleton<MaildirWatcher>();

builder.Services.AddHostedService<MessageIngestService>();

// Liveness beat on its own timer, deliberately independent of the scan loop:
// a multi-minute full scan must not be able to starve the beat and fake a
// dead indexer. See ServiceHeartbeat.
builder.Services.AddHostedService(sp => new HeartbeatService(
    sp.GetRequiredService<SchemaMigrator>(),
    sp.GetRequiredService<MetadataRepository>(),
    ServiceHeartbeat.Indexer,
    sp.GetRequiredService<ILogger<HeartbeatService>>()));

var host = builder.Build();

// Log resolved DB / Maildir paths at startup so the operator can confirm
// which dataset (prod vs test) the service is running against.
{
    var archive = host.Services.GetRequiredService<IOptions<ArchiveOptions>>().Value;
    var ingest = host.Services.GetRequiredService<IOptions<IngestOptions>>().Value;
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Mailvec.Indexer");
    logger.LogInformation(
        "Indexer starting (database={DatabasePath}, maildir={MaildirPath})",
        PathExpansion.Expand(archive.DatabasePath),
        PathExpansion.Expand(ingest.MaildirRoot));
}

host.Run();
