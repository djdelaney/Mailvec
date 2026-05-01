using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Indexer.Services;

var builder = Host.CreateApplicationBuilder(args);

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
host.Run();
