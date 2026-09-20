using Mailvec.Parsing;
using Mailvec.Parsing.Contracts;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Mailvec.Parse;

/// <summary>
/// Composes the host. Separate from <c>Program.cs</c> so tests can start it on
/// a random port (<paramref name="url"/>) with a substituted parser and
/// overridden options, and so the production entry point stays one line.
/// </summary>
public static class ParseHost
{
    public static WebApplication Build(
        string[] args,
        IMailParser? parser = null,
        Action<ParseHostOptions>? configure = null,
        string? url = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        var options = new ParseHostOptions();
        builder.Configuration.GetSection(ParseHostOptions.SectionName).Bind(options);
        configure?.Invoke(options);

        var attachmentMaxBytes = builder.Configuration.GetValue<long?>("Indexer:AttachmentMaxBytes")
            ?? ParseHostOptions.DefaultAttachmentMaxBytes;

        builder.WebHost.UseUrls(url ?? $"http://{options.BindAddress}:{options.Port}");
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes);
        // Exits are deliberate and frequent (timeout, request budget); don't
        // sit in graceful shutdown waiting on a parse thread that will never
        // finish.
        builder.Host.ConfigureHostOptions(h => h.ShutdownTimeout = TimeSpan.FromSeconds(5));

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<RequestBudget>();
        builder.Services.AddSingleton(new ParseGate(Math.Max(1, options.MaxConcurrentParses)));
        if (parser is not null)
            builder.Services.AddSingleton(parser);
        else
            builder.Services.AddSingleton<IMailParser>(sp => new InProcessParser(
                new InProcessParserSettings(attachmentMaxBytes),
                sp.GetRequiredService<ILoggerFactory>()));

        var app = builder.Build();
        ParseEndpoints.Map(app);

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
            app.Logger.LogInformation(
                "parse: listening on {Addresses}; request timeout {Timeout}s, exit after {MaxRequests} requests, body cap {BodyCap} MB, attachment gate {AttachmentGate} MB, {Concurrency} concurrent parse(s) (slot wait {SlotWait}s), parser {Mode}",
                addresses is null ? "?" : string.Join(", ", addresses),
                options.RequestTimeoutSeconds, options.MaxRequestsBeforeExit,
                options.MaxRequestBodyBytes / (1024 * 1024), attachmentMaxBytes / (1024 * 1024),
                Math.Max(1, options.MaxConcurrentParses), options.SlotWaitSeconds,
                app.Services.GetRequiredService<IMailParser>().Mode);
        });

        return app;
    }

    /// <summary>The address the running host actually bound (port 0 resolved), for tests.</summary>
    public static string BoundAddress(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
}
