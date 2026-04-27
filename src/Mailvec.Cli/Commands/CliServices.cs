using Mailvec.Core.Data;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Mailvec.Core.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OllamaOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(Math.Max(5, opts.RequestTimeoutSeconds));
        });

        return services.BuildServiceProvider();
    }
}
