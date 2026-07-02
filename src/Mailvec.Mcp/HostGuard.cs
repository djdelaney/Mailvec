namespace Mailvec.Mcp;

/// <summary>
/// DNS-rebinding / same-origin defense for the loopback HTTP surface.
///
/// The server binds 127.0.0.1, which stops other <em>hosts</em> from routing to
/// the port — but it does not stop a browser on the same machine. A page on
/// evil.com can hold a connection, let its DNS TTL expire, re-resolve evil.com
/// to 127.0.0.1, and then issue requests to :3333 that the browser treats as
/// same-origin — so page JS can read the response (mail bodies via /tray/email,
/// IMAP username via /tray/system) and POST to mutating endpoints (/tray/control
/// stops the launchd agents; /tray/attachment writes files).
///
/// We defend by pinning the Host header (and the Origin header, when a browser
/// sends one) to an allowlist. After a rebind the browser still sends
/// "Host: evil.com", so the request is rejected before it reaches any handler.
/// Loopback names are always allowed; an operator fronting the server with a
/// real hostname (the Cloudflare/container future) adds it via Mcp:AllowedHosts.
/// Native clients (Claude Code's MCP transport, the SwiftUI tray's URLSession)
/// connect to 127.0.0.1/localhost and send no Origin, so they are unaffected.
/// </summary>
public static class HostGuard
{
    private static readonly string[] Loopback = ["localhost", "127.0.0.1", "::1"];

    public static HashSet<string> BuildAllowedHosts(IEnumerable<string>? configured)
    {
        var set = new HashSet<string>(Loopback, StringComparer.OrdinalIgnoreCase);
        if (configured is not null)
        {
            foreach (var h in configured)
            {
                var trimmed = h?.Trim();
                if (!string.IsNullOrEmpty(trimmed)) set.Add(trimmed);
            }
        }
        return set;
    }

    /// <param name="host">The Host header's host component with the port stripped (HostString.Host).</param>
    /// <param name="origin">The raw Origin header value, or null/empty when absent.</param>
    public static bool IsAllowed(string? host, string? origin, HashSet<string> allowedHosts)
    {
        if (string.IsNullOrEmpty(host) || !allowedHosts.Contains(host)) return false;

        // Origin is present on browser-initiated cross-site (and many same-site)
        // requests; native clients omit it. When present it must resolve to an
        // allowed host too — this catches a direct cross-origin POST that the
        // Host check alone (same rebound host) would let through.
        if (!string.IsNullOrEmpty(origin))
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)) return false;
            if (!allowedHosts.Contains(o.Host)) return false;
        }
        return true;
    }
}
