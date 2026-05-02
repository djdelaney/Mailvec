using Mailvec.Core;
using Mailvec.Core.Data;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Mailvec.Core.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailvec.Cli.Commands;

internal static class CliServices
{
    public static ServiceProvider Build()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        }));
        services.Configure<ArchiveOptions>(config.GetSection(ArchiveOptions.SectionName));
        services.Configure<IngestOptions>(config.GetSection(IngestOptions.SectionName));
        services.Configure<OllamaOptions>(config.GetSection(OllamaOptions.SectionName));
        services.Configure<FastmailOptions>(config.GetSection(FastmailOptions.SectionName));

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

        var sp = services.BuildServiceProvider();

        // Print the resolved DB / Maildir paths to stderr so every CLI command
        // surfaces which dataset (prod vs test) it ran against, without
        // polluting stdout (search results, JSON output, etc. flow there).
        var archive = sp.GetRequiredService<IOptions<ArchiveOptions>>().Value;
        var ingest = sp.GetRequiredService<IOptions<IngestOptions>>().Value;
        Console.Error.WriteLine(
            $"[mailvec] db={PathExpansion.Expand(archive.DatabasePath)} maildir={PathExpansion.Expand(ingest.MaildirRoot)}");

        return sp;
    }
}
