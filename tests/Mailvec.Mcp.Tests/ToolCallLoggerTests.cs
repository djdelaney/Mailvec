using Mailvec.Core.Options;
using Mailvec.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailvec.Mcp.Tests;

public class ToolCallLoggerTests
{
    [Fact]
    public void Enabled_mirrors_McpOptions_LogToolCalls()
    {
        var on = new ToolCallLogger(new RecordingLogger(), Options.Create(new McpOptions { LogToolCalls = true }));
        var off = new ToolCallLogger(new RecordingLogger(), Options.Create(new McpOptions { LogToolCalls = false }));

        on.Enabled.ShouldBeTrue();
        off.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void LogResult_always_emits_timing_line()
    {
        var sink = new RecordingLogger();
        var logger = new ToolCallLogger(sink, Options.Create(new McpOptions { LogToolCalls = false }));

        var ts = logger.LogCall("search_emails", new { q = "x" });
        logger.LogResult("search_emails", new { count = 0 }, ts);

        // Disabled → no mcp-call / mcp-result, only mcp-tool timing line.
        sink.Messages.ShouldContain(m => m.Contains("mcp-tool"));
        sink.Messages.ShouldNotContain(m => m.Contains("mcp-call"));
        sink.Messages.ShouldNotContain(m => m.Contains("mcp-result"));
    }

    [Fact]
    public void LogCall_and_LogResult_emit_full_args_and_result_when_enabled()
    {
        var sink = new RecordingLogger();
        var logger = new ToolCallLogger(sink, Options.Create(new McpOptions { LogToolCalls = true }));

        var ts = logger.LogCall("get_email", new { id = 42 });
        logger.LogResult("get_email", new { count = 1 }, ts);

        sink.Messages.ShouldContain(m => m.Contains("mcp-call") && m.Contains("get_email"));
        sink.Messages.ShouldContain(m => m.Contains("mcp-tool"));
        sink.Messages.ShouldContain(m => m.Contains("mcp-result"));
    }

    [Fact]
    public void Serialization_failure_is_swallowed_and_replaced_with_marker()
    {
        var sink = new RecordingLogger();
        var logger = new ToolCallLogger(sink, Options.Create(new McpOptions { LogToolCalls = true }));

        // A self-referential object will throw on JsonSerializer.Serialize;
        // ToolCallLogger catches and substitutes "<serialization-error>".
        logger.LogCall("oops", new SelfReferential());

        sink.Messages.ShouldContain(m => m.Contains("<serialization-error>"));
    }

    private sealed class SelfReferential
    {
        public SelfReferential Self => this;
    }

    private sealed class RecordingLogger : ILogger<ToolCallLogger>
    {
        public List<string> Messages { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
