namespace Mailvec.Core.Options;

public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3333;
    public int SearchDefaultLimit { get; set; } = 20;
    public int SearchMaxLimit { get; set; } = 100;

    /// <summary>
    /// When true, the MCP server emits one INFO log line per tool invocation showing
    /// the arguments and a small result summary. Useful for capturing real Claude
    /// usage patterns to iterate on tool result quality. Off by default.
    /// </summary>
    public bool LogToolCalls { get; set; }

    /// <summary>
    /// Where get_attachment writes extracted attachment files. The default is
    /// inside ~/Downloads so the user can find files in Finder / their browser's
    /// Downloads list. Avoid ~/Library/Caches (hidden from users) and
    /// ~/Documents (TCC-blocked from Claude Desktop's spawned processes).
    /// </summary>
    public string AttachmentDownloadDir { get; set; } = "~/Downloads/mailvec";

    /// <summary>
    /// For text-ish content types under this many bytes, get_attachment also
    /// returns the decoded UTF-8 text inline as a separate text content block.
    /// Convenience for CSV / JSON / logs so Claude can read them in one round
    /// trip without invoking a filesystem MCP. 0 disables the extra text block.
    /// </summary>
    public int AttachmentInlineTextMaxBytes { get; set; } = 256 * 1024;
}
