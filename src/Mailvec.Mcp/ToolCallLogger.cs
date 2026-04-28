using System.Text.Json;
using System.Text.Json.Serialization;
using Mailvec.Core.Options;
using Microsoft.Extensions.Options;

namespace Mailvec.Mcp;

/// <summary>
/// Optional per-call logging for the four MCP tools, gated by Mcp:LogToolCalls.
/// When enabled, emits one "mcp-call" INFO line at the start of each invocation
/// (the args Claude chose) and one "mcp-result" INFO line on success (a small
/// summary of what we returned). Designed to produce a grep-friendly corpus of
/// real Claude usage so result quality can be iterated on. Errors are not
/// caught here — exceptions surface through the normal MCP/logging path, and
/// the absence of an "mcp-result" line is itself a signal.
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

    public void LogCall(string tool, object args)
    {
        if (!Enabled) return;
        _logger.LogInformation("mcp-call tool={Tool} args={Args}", tool, Serialize(args));
    }

    public void LogResult(string tool, object summary)
    {
        if (!Enabled) return;
        _logger.LogInformation("mcp-result tool={Tool} result={Result}", tool, Serialize(summary));
    }

    private static string Serialize(object o)
    {
        try { return JsonSerializer.Serialize(o, JsonOpts); }
        catch { return "<serialization-error>"; }
    }
}
