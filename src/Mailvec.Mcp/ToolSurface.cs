using Mailvec.Mcp.Tools;

namespace Mailvec.Mcp;

/// <summary>
/// Maps the locked MCP tool names (see CLAUDE.md "MCP API stability") to
/// their implementing classes, and resolves which of them to register given
/// <c>Mcp:DisabledTools</c>. A tool disabled here is absent from tools/list
/// AND tools/call (the SDK rejects calls to unregistered tools), uniformly
/// across whatever OAuth front sits in front.
///
/// <para>The tools this exists for are <c>view_attachment</c> and
/// <c>get_attachment_page_image</c>: both feed attacker-supplied mail bytes to
/// native parsers (PDFium/SkiaSharp). Neither returns a whole raw document —
/// <c>get_attachment_page_image</c> returns one rendered JPEG page, and
/// <c>view_attachment</c> returns an inline image or small decoded text and
/// deliberately never ships arbitrary binary. The native parsing is the reason
/// to be able to drop them, not the payload.</para>
///
/// <para><b>The live tunnel deployment does not drop them</b> — compose.yml
/// stages both as commented-out entries and leaves them off as an accepted
/// risk, since the embedder's OCR pass reaches the same parsers unattended
/// anyway. See <c>McpOptions.DisabledTools</c> and docs/security.md "What's
/// accepted" for the conditions that reverse that call.</para>
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
