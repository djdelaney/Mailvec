using Mailvec.Core;
using Mailvec.Core.Data;
using Mailvec.Core.Logging;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Indexer.Services;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
SerilogSetup.Configure(builder.Services, builder.Configuration, builder.Logging, "indexer");

builder.Services.Configure<ArchiveOptions>(builder.Configuration.GetSection(ArchiveOptions.SectionName));
builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection(IngestOptions.SectionName));
builder.Services.Configure<IndexerOptions>(builder.Configuration.GetSection(IndexerOptions.SectionName));

builder.Services.AddSingleton<ConnectionFactory>();
builder.Services.AddSingleton<SchemaMigrator>();
builder.Services.AddSingleton<MessageRepository>();
builder.Services.AddSingleton<ChunkRepository>();
builder.Services.AddSingleton<SyncStateRepository>();
builder.Services.AddSingleton<MessageParser>();
builder.Services.AddSingleton<MaildirScanner>();
builder.Services.AddSingleton<MaildirWatcher>();

builder.Services.AddHostedService<MessageIngestService>();

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
