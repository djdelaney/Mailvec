using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Mailvec.Embedder.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ArchiveOptions>(builder.Configuration.GetSection(ArchiveOptions.SectionName));
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));
builder.Services.Configure<EmbedderOptions>(builder.Configuration.GetSection(EmbedderOptions.SectionName));

builder.Services.AddSingleton<ConnectionFactory>();
builder.Services.AddSingleton<SchemaMigrator>();
builder.Services.AddSingleton<MessageRepository>();
builder.Services.AddSingleton<MetadataRepository>();
builder.Services.AddSingleton<ChunkRepository>();
builder.Services.AddSingleton<ChunkingService>();

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

builder.Services.AddHostedService<EmbeddingWorker>();

var host = builder.Build();
host.Run();
