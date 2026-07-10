using Mailvec.Cli.Commands;
using Mailvec.Core.Options;
using Mailvec.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailvec.Cli.Tests;

/// <summary>
/// The producer/consumer contract between ToolCallLogger (the MCP server
/// writes "mcp-call tool=... args={json}" lines) and eval-import (the CLI
/// parses them back with two sink-specific regexes). A template or property
/// rename on either side silently breaks `mailvec eval-import` — no error,
/// just "No mcp-call lines found" — so this test drives the REAL producer's
/// rendered output through the REAL consumer.
/// </summary>
public sealed class EvalImportLogContractTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "mailvec-logcontract-" + Guid.NewGuid().ToString("N"));

    public EvalImportLogContractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private sealed class CapturingLogger : ILogger<ToolCallLogger>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public void Tool_call_lines_round_trip_through_the_import_parser()
    {
        // Producer: the real ToolCallLogger, fed the same anonymous-object
        // projection SearchEmailsTool passes to LogCall (keep this in
        // lockstep with SearchEmailsTool.cs — it IS the contract's producer
        // half; if the projection there gains/renames a member, mirror it
        // here so the round-trip stays honest).
        var sink = new CapturingLogger();
        var toolLog = new ToolCallLogger(sink, Options.Create(new McpOptions { LogToolCalls = true }));
        var query = "plumbing leak under kitchen sink";
        toolLog.LogCall("search_emails", new
        {
            query,
            mode = "hybrid",
            limit = 20,
            folder = "INBOX",
            dateFrom = "2026-01-01T00:00:00Z",
            dateTo = (string?)null,
            fromContains = "acme",
            fromExact = (string?)null,
            hasAttachments = (bool?)null,
            attachmentType = (string?)null,
        });
        var rendered = sink.Messages.ShouldHaveSingleItem(); // "mcp-call tool=search_emails args={...}"

        // Consumer: wrap the rendered message in both sink shapes eval-import
        // reads. The prefixes mirror SerilogSetup's file template and
        // Serilog's default console template; the fragile joint this test
        // pins is the message BODY — a rename of the template text or a
        // property in ToolCallLogger breaks both regexes at once.
        File.WriteAllLines(
            Path.Combine(_dir, "mailvec-mcp-20260709.log"),
            [$"2026-07-09 12:00:00.000 -04:00 [INF] Mailvec.Mcp.ToolCallLogger: {rendered}"]);
        var claudeLog = Path.Combine(_dir, "mcp-server-mailvec.log");
        File.WriteAllLines(claudeLog, [$"[12:00:01 INF] {rendered}"]);

        var calls = EvalImportCommand.LoadRecentCalls(_dir, claudeLog, limit: 10);

        // Both sources parsed, and the identical args deduped to one call.
        var call = calls.ShouldHaveSingleItem();
        call.Args.Query.ShouldBe(query);
        call.Args.Mode.ShouldBe("hybrid");
        call.Args.Limit.ShouldBe(20);
        call.Args.Folder.ShouldBe("INBOX");
        call.Args.DateFrom.ShouldBe("2026-01-01T00:00:00Z");
        call.Args.FromContains.ShouldBe("acme");
        call.Args.DateTo.ShouldBeNull();
        call.Args.FromExact.ShouldBeNull();
    }

    [Fact]
    public void Distinct_calls_from_the_two_sinks_both_surface()
    {
        var sink = new CapturingLogger();
        var toolLog = new ToolCallLogger(sink, Options.Create(new McpOptions { LogToolCalls = true }));
        toolLog.LogCall("search_emails", new { query = "from the http server", mode = "hybrid" });
        toolLog.LogCall("search_emails", new { query = "from the stdio bundle", mode = "keyword" });

        File.WriteAllLines(
            Path.Combine(_dir, "mailvec-mcp-20260709.log"),
            [$"2026-07-09 12:00:00.000 -04:00 [INF] Mailvec.Mcp.ToolCallLogger: {sink.Messages[0]}"]);
        var claudeLog = Path.Combine(_dir, "mcp-server-mailvec.log");
        File.WriteAllLines(claudeLog, [$"[12:00:01 INF] {sink.Messages[1]}"]);

        var calls = EvalImportCommand.LoadRecentCalls(_dir, claudeLog, limit: 10);

        calls.Count.ShouldBe(2);
        calls.Select(c => c.Args.Query).ShouldBe(
            ["from the http server", "from the stdio bundle"], ignoreOrder: true);
    }
}
