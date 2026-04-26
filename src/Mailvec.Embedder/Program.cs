using Mailvec.Core.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ArchiveOptions>(builder.Configuration.GetSection(ArchiveOptions.SectionName));
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));
builder.Services.Configure<EmbedderOptions>(builder.Configuration.GetSection(EmbedderOptions.SectionName));

// TODO Phase 2: register Ollama HttpClient, ChunkingService, EmbeddingWorker

var host = builder.Build();
host.Run();
