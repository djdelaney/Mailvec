using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mailvec.Core.Options;
using Microsoft.Extensions.Options;

namespace Mailvec.Mcp;

/// <summary>
/// Per-call logging, taken as a dependency by every registered MCP tool (see
/// <see cref="ToolSurface.All"/> for the current set). The "mcp-tool" timing line is
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

    /// <param name="count">Result count, when the tool has one. Non-PII, so it
    /// rides the always-on timing line.</param>
    /// <param name="mode">Search mode, when the tool has one. Non-PII.</param>
    public void LogResult(string tool, object summary, long startTimestamp, int? count = null, string? mode = null)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        // Count and mode are deliberately on the UNCONDITIONAL line: "was that
        // search slow, and did it return anything?" is the usual diagnostic
        // question, and answering it without Mcp:LogToolCalls is the point —
        // that flag logs subjects, sender addresses and filenames, and every
        // extra reason to switch it on is a reason mail PII ends up in
        // `docker logs`. See docs/security.md "What's accepted".
        if (count is null && mode is null)
        {
            _logger.LogInformation("mcp-tool tool={Tool} elapsedMs={ElapsedMs:F1}", tool, elapsedMs);
        }
        else
        {
            _logger.LogInformation(
                "mcp-tool tool={Tool} elapsedMs={ElapsedMs:F1} count={Count} mode={Mode}",
                tool, elapsedMs, count, mode);
        }
        if (Enabled)
            _logger.LogInformation("mcp-result tool={Tool} result={Result}", tool, Serialize(summary));
    }

    private static string Serialize(object o)
    {
        try { return JsonSerializer.Serialize(o, JsonOpts); }
        catch { return "<serialization-error>"; }
    }
}
