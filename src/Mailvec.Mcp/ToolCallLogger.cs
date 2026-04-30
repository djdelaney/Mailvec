using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mailvec.Core.Options;
using Microsoft.Extensions.Options;

namespace Mailvec.Mcp;

/// <summary>
/// Per-call logging for the four MCP tools. The "mcp-tool" timing line is
/// emitted unconditionally so latency anomalies are visible in normal
/// operation; the "mcp-call" args line and "mcp-result" body summary are
/// gated by Mcp:LogToolCalls and intended for usage-pattern capture. Errors
/// are not caught here — exceptions surface through the normal MCP/logging
/// path, and the absence of a trailing "mcp-tool" line is itself a signal.
/// </summary>
public sealed class ToolCallLogger
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly ILogger<ToolCallLogger> _logger;

    public bool Enabled { get; }

    public ToolCallLogger(ILogger<ToolCallLogger> logger, IOptions<McpOptions> options)
    {
        _logger = logger;
        Enabled = options.Value.LogToolCalls;
    }

    public long LogCall(string tool, object args)
    {
        if (Enabled)
            _logger.LogInformation("mcp-call tool={Tool} args={Args}", tool, Serialize(args));
        return Stopwatch.GetTimestamp();
    }

    public void LogResult(string tool, object summary, long startTimestamp)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        _logger.LogInformation("mcp-tool tool={Tool} elapsedMs={ElapsedMs:F1}", tool, elapsedMs);
        if (Enabled)
            _logger.LogInformation("mcp-result tool={Tool} result={Result}", tool, Serialize(summary));
    }

    private static string Serialize(object o)
    {
        try { return JsonSerializer.Serialize(o, JsonOpts); }
        catch { return "<serialization-error>"; }
    }
}
