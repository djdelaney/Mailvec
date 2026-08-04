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
    /// Empty (the default) keeps the full surface.
    ///
    /// <para>This is a <b>conditional</b> hardening control, not a requirement
    /// of internet exposure. The live tunnel deployment runs with it empty:
    /// compose.yml stages <c>view_attachment</c> and
    /// <c>get_attachment_page_image</c> as commented-out entries and leaves them
    /// off, because the embedder's OCR pass already feeds the same native
    /// parsers (PDFium/SkiaSharp) unattended for every scanned attachment that
    /// arrives by mail — so the trim would close the smaller, attended half of
    /// an exposure that stays open either way. docs/security.md "What's
    /// accepted" records that decision and the conditions that reverse it (a
    /// published host port, a non-owner-equivalent caller clearing Access, the
    /// tunnel no longer 404-ing the unauthenticated surfaces, or a mutating tool
    /// landing). Uncomment those entries if any of them come true.</para>
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
    /// and <c>/up</c> are mapped separately and are unaffected.
    ///
    /// <para>**This is enforced, not merely defaulted.** Leaving it true on a
    /// server that isn't loopback-only — a non-loopback <see cref="BindAddress"/>,
    /// or a non-loopback name in <see cref="AllowedHosts"/> — makes the MCP
    /// server refuse to start (<c>TrayExposureGuard</c>). Note the bind address
    /// is the signal that matters: HostGuard always admits the loopback Host
    /// names, so a 0.0.0.0 bind is reachable by anything that can route to the
    /// port even with AllowedHosts entirely empty.</para>
    /// </summary>
    public bool EnableTrayEndpoints { get; set; } = true;

    /// <summary>
    /// Cloudflare Access assertion validation at the origin. Off by default —
    /// see <see cref="AccessOptions"/> for why that's the right default rather
    /// than a gap.
    /// </summary>
    public AccessOptions Access { get; set; } = new();

    /// <summary>
    /// Serve <c>/health</c> only to callers on the loopback interface; everyone
    /// else gets a 404. Default true, and it costs nothing because **every
    /// documented consumer is already loopback**: the compose healthcheck curls
    /// <c>127.0.0.1:3333/health</c> from inside the mcp container,
    /// <c>mailvec doctor</c>'s probe rewrites a <c>0.0.0.0</c> bind to
    /// <c>127.0.0.1</c> (<c>DoctorCommand.HealthProbeUrl</c>), and the tray polls
    /// loopback on local installs. Nothing off-box has ever needed this body.
    ///
    /// <para>What it's for: <c>/health</c> is the detailed sibling of
    /// <c>/up</c> and discloses the archive's filesystem path, corpus counts,
    /// embedding model identity, and — the one that matters — the internal
    /// Ollama LAN address. <c>/up</c> exists precisely so that an external
    /// monitor never needs any of that, so forwarding <c>/health</c> off-box
    /// hands out the disclosure <c>/up</c> was built to avoid.</para>
    ///
    /// <para>404 rather than 403, matching how the tunnel treats
    /// <c>/tray/</c>: a refusal confirms the endpoint is there, and there is no
    /// caller who benefits from learning that.</para>
    ///
    /// <para>This is the load-bearing barrier, in the same sense as
    /// <see cref="EnableTrayEndpoints"/>: it's server-side, so it holds
    /// regardless of what the tunnel's ingress rules say. The matching
    /// <c>health</c> 404 rule at the tunnel is defense in depth. Set false only
    /// if something genuinely off-box needs the detailed body — and prefer
    /// pointing it at <c>/up</c> instead.</para>
    /// </summary>
    public bool RestrictHealthToLoopback { get; set; } = true;

    public int SearchDefaultLimit { get; set; } = 20;
    public int SearchMaxLimit { get; set; } = 100;

    /// <summary>
    /// Maximum messages <c>get_thread</c> returns in one response. Every other
    /// mail-bearing tool is bounded by something the caller passes — search by
    /// <see cref="SearchMaxLimit"/>, attachment text by its <c>maxChars</c>
    /// window — but a thread's size is chosen by whoever replied to it, not by
    /// the caller, so without a cap the response size is an input from the
    /// senders. Mailing lists and long CC chains are the realistic shape.
    /// Truncation is chronological-prefix (oldest kept) and always reported:
    /// the response carries <c>truncated</c> plus a <c>totalCount</c> that
    /// exceeds <c>count</c>, so the model can say so rather than silently
    /// summarising half a thread.
    /// </summary>
    public int ThreadMaxMessages { get; set; } = 100;

    /// <summary>
    /// Aggregate cap, in characters, on the body text <c>get_thread</c> returns
    /// when <c>includeBodies=true</c> — the message cap alone doesn't bound the
    /// response, since 100 messages carrying a 2 MB quoted-history tail each is
    /// still unbounded in bytes. Spent oldest-first; once exhausted, later
    /// entries get a truncated (possibly empty) body with <c>bodyTruncated</c>
    /// set, and the thread's <c>truncated</c> flag is raised. ~200k chars is
    /// roughly 50k tokens — already the outer edge of what one tool result
    /// should carry, and get_email reaches any individual body in full.
    /// </summary>
    public int ThreadMaxBodyChars { get; set; } = 200_000;

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
