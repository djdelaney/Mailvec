namespace Mailvec.Core.Options;

public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3333;

    /// <summary>
    /// Extra Host-header hostnames accepted by the DNS-rebinding guard, on top
    /// of the always-allowed loopback names (localhost / 127.0.0.1 / ::1).
    /// Leave empty for the loopback-only deployment; when fronting the server
    /// with a real hostname (a Cloudflare tunnel / container ingress), add that
    /// hostname here so its requests aren't rejected. See HostGuard.
    /// </summary>
    public string[] AllowedHosts { get; set; } = [];

    /// <summary>
    /// Tool names to remove from this deployment's MCP surface — absent from
    /// tools/list and rejected on tools/call. Names must match the locked
    /// tool-name contract exactly; an unknown name fails startup (a typo
    /// would otherwise silently leave the tool it meant to disable exposed).
    /// Intended for internet-fronted deployments: docs/security.md requires
    /// dropping view_attachment and get_attachment_page_image (native parsers
    /// fed by mail bytes; whole raw documents) from any tunnel-exposed
    /// surface. Empty (the default) keeps the full surface.
    /// </summary>
    public string[] DisabledTools { get; set; } = [];

    /// <summary>
    /// Whether to map the plain-REST <c>/tray/*</c> endpoints (consumed only by
    /// the macOS menu-bar tray app). Default true for the loopback / launchd
    /// install. **Set false on any internet-fronted deployment** — the tray
    /// surface is unauthenticated at the origin and returns mail content
    /// (<c>/tray/email/{id}</c> = full bodies, <c>/tray/folders</c> = folder map,
    /// <c>/tray/search</c> = full-text search, <c>/tray/system</c> = IMAP
    /// account), yet nothing consumes it in a container (the tray is a local
    /// macOS client). Disabling it at the origin is defense-in-depth that holds
    /// even if the tunnel's path-404 rule is ever wrong — the same
    /// server-side-authoritative reasoning as <see cref="DisabledTools"/>. The
    /// container image bakes this to false; see docs/security.md. <c>/health</c>
    /// is mapped separately and is unaffected.
    /// </summary>
    public bool EnableTrayEndpoints { get; set; } = true;

    public int SearchDefaultLimit { get; set; } = 20;
    public int SearchMaxLimit { get; set; } = 100;

    /// <summary>
    /// When true, the MCP server emits one INFO log line per tool invocation showing
    /// the arguments and a small result summary. Useful for capturing real Claude
    /// usage patterns to iterate on tool result quality. Off by default.
    /// </summary>
    public bool LogToolCalls { get; set; }

    /// <summary>
    /// Where the explicit save-to-disk paths (the tray's Save button and
    /// `mailvec extract-attachments`) write attachment files — the MCP tools
    /// never write here. The default is inside ~/Downloads so the user can find
    /// files in Finder / their browser's Downloads list. Avoid ~/Library/Caches
    /// (hidden from users) and ~/Documents (TCC-blocked from Claude Desktop's
    /// spawned processes).
    /// </summary>
    public string AttachmentDownloadDir { get; set; } = "~/Downloads/mailvec";

    /// <summary>
    /// For text-ish content types under this many bytes, view_attachment also
    /// returns the decoded UTF-8 text inline as a separate text content block.
    /// Convenience for CSV / JSON / logs so Claude can read them in one round
    /// trip without invoking a filesystem MCP. 0 disables the extra text block.
    /// </summary>
    public int AttachmentInlineTextMaxBytes { get; set; } = 256 * 1024;
}
