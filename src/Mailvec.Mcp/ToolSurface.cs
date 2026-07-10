using Mailvec.Mcp.Tools;

namespace Mailvec.Mcp;

/// <summary>
/// Maps the locked MCP tool names (see CLAUDE.md "MCP API stability") to
/// their implementing classes, and resolves which of them to register given
/// <c>Mcp:DisabledTools</c>. Disabling is the server-side half of trimming
/// the remote tool surface before the Cloudflare tunnel goes live —
/// <c>view_attachment</c> and <c>get_attachment_page_image</c> feed
/// attacker-supplied mail bytes to native parsers (PDFium/SkiaSharp) and
/// return whole raw documents, which docs/security.md rules out for an
/// internet-reachable endpoint. A tool disabled here is absent from
/// tools/list AND tools/call (the SDK rejects calls to unregistered tools),
/// uniformly across whatever OAuth front sits in front.
/// </summary>
internal static class ToolSurface
{
    // Keep in lockstep with the [McpServerTool(Name = ...)] attributes; a
    // test reflects the attributes and fails on drift.
    internal static readonly IReadOnlyDictionary<string, Type> All =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            ["search_emails"] = typeof(SearchEmailsTool),
            ["get_email"] = typeof(GetEmailTool),
            ["get_thread"] = typeof(GetThreadTool),
            ["list_folders"] = typeof(ListFoldersTool),
            ["view_attachment"] = typeof(ViewAttachmentTool),
            ["get_attachment_text"] = typeof(GetAttachmentTextTool),
            ["get_attachment_page_image"] = typeof(GetAttachmentPageImageTool),
        };

    /// <summary>
    /// The tool classes to register after removing <paramref name="disabledTools"/>.
    /// Throws on a name that isn't a known tool: this option exists for
    /// security posture, and a typo'd entry would silently leave the tool it
    /// meant to disable exposed — fail startup loudly instead (the container
    /// restart policy makes that visible immediately).
    /// </summary>
    internal static IReadOnlyList<Type> Resolve(IEnumerable<string>? disabledTools)
    {
        var disabled = new HashSet<string>(
            (disabledTools ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var unknown = disabled.Where(n => !All.ContainsKey(n)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"Mcp:DisabledTools contains unknown tool name(s): {string.Join(", ", unknown)}. " +
                $"Valid names: {string.Join(", ", All.Keys.OrderBy(k => k, StringComparer.Ordinal))}. " +
                "Refusing to start — a typo here would silently leave the tool it meant to disable exposed.");
        }

        return All.Where(kv => !disabled.Contains(kv.Key)).Select(kv => kv.Value).ToList();
    }
}
