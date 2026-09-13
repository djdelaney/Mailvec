using Mailvec.Core.Options;
using Mailvec.Parsing.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Parsing;

/// <summary>
/// The one place that decides which <see cref="IMailParser"/> a process gets —
/// the <c>VisionRegistration</c> pattern applied a third time, so the indexer,
/// embedder, MCP server and CLI can never resolve different parsers.
///
/// Core never names the in-process implementation type: each host passes a
/// factory (<c>(sp, settings) => new InProcessParser(settings, …)</c>), which
/// is invoked only when <c>Parser:Mode</c> is <c>inprocess</c>. In remote mode
/// the lambda is never called, so the parser assemblies are never loaded —
/// which is what lets the container image strip them from the privileged
/// binaries' directories and turn an accidental in-process mode there into a
/// loud <see cref="System.IO.FileNotFoundException"/> at first parse.
/// </summary>
public static class ParserRegistration
{
    public const string InProcessMode = "inprocess";
    public const string RemoteMode = "remote";

    public static IServiceCollection AddMailvecParser(
        this IServiceCollection services,
        IConfiguration config,
        Func<IServiceProvider, InProcessParserSettings, IMailParser> inProcessFactory)
    {
        ArgumentNullException.ThrowIfNull(inProcessFactory);
        services.Configure<ParserOptions>(config.GetSection(ParserOptions.SectionName));

        services.AddSingleton<IMailParser>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ParserOptions>>().Value;
            var mode = options.Mode?.Trim().ToLowerInvariant();
            switch (mode)
            {
                case null or "" or InProcessMode:
                {
                    // The extractor's size gate is configured under Indexer:*
                    // (the indexer is where it historically lived); read it
                    // here rather than making every host bind IndexerOptions.
                    var indexer = new IndexerOptions();
                    config.GetSection(IndexerOptions.SectionName).Bind(indexer);
                    return inProcessFactory(sp, new InProcessParserSettings(indexer.AttachmentMaxBytes));
                }
                case RemoteMode:
                    throw new NotSupportedException(
                        "Parser:Mode=remote is not available yet — it lands in phase 2 of " +
                        "docs/proposals/attachment-parser-isolation.md. Use 'inprocess'.");
                default:
                    throw new InvalidOperationException(
                        $"Unknown Parser:Mode '{options.Mode}'. Expected '{InProcessMode}' or '{RemoteMode}'.");
            }
        });

        return services;
    }
}
