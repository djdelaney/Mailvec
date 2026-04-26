using Mailvec.Core.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ArchiveOptions>(builder.Configuration.GetSection(ArchiveOptions.SectionName));
builder.Services.Configure<IndexerOptions>(builder.Configuration.GetSection(IndexerOptions.SectionName));

// TODO Phase 1: register MaildirScanner, MaildirWatcher, MessageIngestService

var host = builder.Build();
host.Run();
