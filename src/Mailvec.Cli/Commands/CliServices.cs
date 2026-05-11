using Mailvec.Core;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Eval;
using Mailvec.Core.Health;
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
        services.AddLogging(b =>
        {
            b.AddConfiguration(config.GetSection("Logging"));
            b.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        });
        services.Configure<ArchiveOptions>(config.GetSection(ArchiveOptions.SectionName));
        services.Configure<IngestOptions>(config.GetSection(IngestOptions.SectionName));
        services.Configure<OllamaOptions>(config.GetSection(OllamaOptions.SectionName));
        services.Configure<FastmailOptions>(config.GetSection(FastmailOptions.SectionName));
        // IndexerOptions carries AttachmentMaxBytes; only `extract-attachments`
        // currently reads it from the CLI side, but binding it here is cheap
        // and keeps the section name resolvable for future commands.
        services.Configure<IndexerOptions>(config.GetSection(IndexerOptions.SectionName));
        // McpOptions is bound so `mailvec doctor` can probe the running HTTP
        // server's /health at the configured BindAddress:Port — same address
        // the launchd plist uses, so checking from the CLI matches what
        // external monitors would see.
        services.Configure<McpOptions>(config.GetSection(McpOptions.SectionName));

        services.AddSingleton<ConnectionFactory>();
        services.AddSingleton<SchemaMigrator>();
        services.AddSingleton<MessageRepository>();
        services.AddSingleton<MetadataRepository>();
        services.AddSingleton<ChunkRepository>();
        services.AddSingleton<KeywordSearchService>();
        services.AddSingleton<VectorSearchService>();
        services.AddSingleton<HybridSearchService>();
        // Ranking seam for `mailvec eval` — production wires the three sealed
        // search services; the test suite substitutes a fake to unit-test
        // EvalRunner's orchestration without a DB.
        services.AddSingleton<IEvalRankingSource, DbEvalRankingSource>();
        // The extractor is shared between the indexer (during ingest) and the
        // CLI's `extract-attachments` backfill command. Pure CPU work, no I/O
        // beyond what's handed in via MimeKit, so it's safe to wire here even
        // for commands that don't use it.
        services.AddSingleton<AttachmentTextExtractor>();
        // HealthService computes the same DB / embedding / Ollama snapshot the
        // MCP /health endpoint returns. `mailvec doctor` reuses it so the CLI
        // and HTTP views can never disagree about what "healthy" means.
        services.AddSingleton<HealthService>();

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
