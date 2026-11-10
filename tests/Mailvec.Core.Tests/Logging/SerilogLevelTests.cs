using Mailvec.Core.Logging;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Mailvec.Core.Tests.Logging;

/// <summary>
/// <c>Logging:LogLevel</c> was dead configuration: AddSerilog replaces the
/// logger factory, so nothing read it and every service logged at
/// Information whatever its appsettings said. The visible cost was HttpClient
/// writing the Ollama or hosted-provider URL four times per request.
/// </summary>
public class SerilogLevelTests
{
    private const string Http = "System.Net.Http.HttpClient.embedding.LogicalHandler";
    private const string Ours = "Mailvec.Mcp.Tools.SearchEmailsTool";

    private sealed class Collector : ILogEventSink
    {
        public List<(string Source, LogEventLevel Level)> Events { get; } = [];

        public void Emit(LogEvent e) =>
            Events.Add((e.Properties["SourceContext"].ToString().Trim('"'), e.Level));
    }

    /// <summary>What reaches a sink for one event per (source, level), under the given Logging:LogLevel entries.</summary>
    private static List<(string Source, LogEventLevel Level)> Emitted(params (string Key, string Value)[] levels)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(levels.ToDictionary(l => "Logging:LogLevel:" + l.Key, l => (string?)l.Value))
            .Build();
        var sink = new Collector();
        using var logger = SerilogSetup.ApplyLevels(new LoggerConfiguration(), configuration).WriteTo.Sink(sink).CreateLogger();

        foreach (var source in new[] { Http, Ours, "Microsoft.AspNetCore.Hosting.Diagnostics", "Polly" })
            foreach (var level in new[] { LogEventLevel.Debug, LogEventLevel.Information, LogEventLevel.Warning })
                logger.ForContext(Constants.SourceContextPropertyName, source).Write(level, "x");
        return sink.Events;
    }

    [Fact]
    public void With_no_configuration_request_logging_is_quiet_and_our_own_is_not()
    {
        var seen = Emitted();

        // The line that carries the endpoint URL.
        seen.ShouldNotContain((Http, LogEventLevel.Information));
        seen.ShouldNotContain(("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Information));
        seen.ShouldNotContain(("Polly", LogEventLevel.Information));
        // A failure still shows, and nothing of ours is lost.
        seen.ShouldContain((Http, LogEventLevel.Warning));
        seen.ShouldContain((Ours, LogEventLevel.Information));
        seen.ShouldNotContain((Ours, LogEventLevel.Debug));
    }

    [Fact]
    public void Default_sets_the_floor_for_everything_not_named()
    {
        var seen = Emitted(("Default", "Debug"));

        seen.ShouldContain((Ours, LogEventLevel.Debug));
        seen.ShouldNotContain((Http, LogEventLevel.Information)); // still quiet unless asked for by name
    }

    [Fact]
    public void A_named_category_can_be_turned_back_up()
    {
        // The documented way to see the endpoint when debugging an outage.
        var seen = Emitted(("System.Net.Http.HttpClient", "Information"));

        seen.ShouldContain((Http, LogEventLevel.Information));
    }

    [Fact]
    public void A_named_category_can_be_turned_down_or_off()
    {
        var seen = Emitted(("Mailvec.Mcp", "Warning"), ("Polly", "None"));

        seen.ShouldNotContain((Ours, LogEventLevel.Information));
        seen.ShouldContain((Ours, LogEventLevel.Warning));
        seen.ShouldNotContain(("Polly", LogEventLevel.Warning));
    }

    [Fact]
    public void An_entry_that_is_not_a_level_is_skipped()
    {
        // The MCP appsettings.json keeps a prose note under a "// …" key in
        // this section, and a typo must not take logging down at startup.
        var seen = Emitted(("// System.Net.Http.HttpClient", "Every /health call pings Ollama"), ("Mailvec", "Loud"), ("Polly", "99"));

        seen.ShouldContain((Ours, LogEventLevel.Information));
        seen.ShouldNotContain((Ours, LogEventLevel.Debug));
        seen.ShouldNotContain(("Polly", LogEventLevel.Information));
    }

    [Fact]
    public void Every_shipped_appsettings_level_is_one_this_understands()
    {
        // A level name the mapping skipped would be dead configuration again.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mailvec.slnx"))) dir = dir.Parent;
        var src = Path.Combine(dir.ShouldNotBeNull().FullName, "src");

        int checkedCount = 0;
        foreach (var file in Directory.EnumerateDirectories(src).Select(d => Path.Combine(d, "appsettings.json")).Where(File.Exists))
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(file).Build();
            foreach (var entry in configuration.GetSection("Logging:LogLevel").GetChildren())
            {
                if (entry.Key.StartsWith("//", StringComparison.Ordinal)) continue;
                Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(entry.Value, ignoreCase: true, out _)
                    .ShouldBeTrue($"{Path.GetFileName(Path.GetDirectoryName(file))}: Logging:LogLevel:{entry.Key} = {entry.Value}");
                checkedCount++;
            }
        }
        checkedCount.ShouldBeGreaterThan(3);
    }
}
