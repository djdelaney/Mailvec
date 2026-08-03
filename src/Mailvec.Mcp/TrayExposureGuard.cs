using System.Net;
using Mailvec.Core.Options;

namespace Mailvec.Mcp;

/// <summary>
/// Startup guard making one configuration unrepresentable: the <c>/tray/*</c>
/// surface enabled on a server that is reachable from anywhere but the local
/// machine.
///
/// <para>Why a hard failure rather than a default. <c>Mcp:EnableTrayEndpoints</c>
/// defaults true (correct for the launchd/loopback install) and the container
/// image bakes it false — but a default is only a default. Anything that
/// re-enables it on a non-loopback deployment (an <c>appsettings.Local.json</c>,
/// a compose <c>environment:</c> line, a hand-rolled image, a stray env var)
/// publishes full message bodies, the folder map, full-text search and the IMAP
/// account with no authentication whatsoever, plus mutating POSTs — and it does
/// so *silently*, because the server starts and serves perfectly happily. Same
/// reasoning as <c>Mcp:DisabledTools</c> rejecting an unknown tool name at
/// startup: for a mistake whose symptom is "your mailbox is on the internet",
/// refusing to boot is the proportionate response.</para>
///
/// <para>What counts as exposed — and why it is NOT "is a public hostname
/// configured". <see cref="HostGuard"/> always admits the loopback Host names
/// regardless of <c>Mcp:AllowedHosts</c>, so a server bound to 0.0.0.0 with an
/// entirely empty AllowedHosts is still reachable by any caller that can route
/// to the port: they simply send <c>Host: localhost</c>. Keying this guard off a
/// configured public hostname would therefore miss the most dangerous shape it
/// exists to catch. The bind address is the load-bearing signal.</para>
///
/// <para>A non-loopback entry in <c>Mcp:AllowedHosts</c> is the second signal.
/// It means something is expected to front this server under a real hostname;
/// on a loopback bind that front is same-host (a local reverse proxy, or
/// cloudflared sharing the network namespace), which reaches <c>/tray/*</c>
/// just as effectively.</para>
///
/// <para>Deliberately not offered: silently forcing the tray to loopback. That
/// trades a loud, one-line-to-fix startup error for an operator debugging a
/// tray app that will never connect, with nothing anywhere saying why.</para>
/// </summary>
public static class TrayExposureGuard
{
    /// <summary>
    /// Returns null when the configuration is safe, or an operator-facing
    /// explanation of why it is not. Pure, so the matrix is unit-testable
    /// without standing up a host.
    /// </summary>
    public static string? Violation(McpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.EnableTrayEndpoints) return null;

        var reasons = new List<string>();

        if (!IsLoopbackBind(options.BindAddress))
            reasons.Add($"Mcp:BindAddress is '{options.BindAddress}', which is not a loopback address");

        var fronting = (options.AllowedHosts ?? [])
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim())
            .Where(h => !HostGuard.Loopback.Contains(h, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (fronting.Length > 0)
            reasons.Add($"Mcp:AllowedHosts admits non-loopback hostname(s): {string.Join(", ", fronting)}");

        if (reasons.Count == 0) return null;

        return
            "Refusing to start: Mcp:EnableTrayEndpoints is true, but this server is not loopback-only (" +
            string.Join("; ", reasons) + "). The /tray/* endpoints have no authentication of their own and " +
            "return mail content — full message bodies, the folder map, full-text search, the IMAP account — " +
            "and accept mutating POSTs, so serving them beyond the local machine publishes the mailbox " +
            "unauthenticated.\n" +
            "Fix by choosing the deployment this is meant to be:\n" +
            "  * Container / fronted / LAN-reachable: set Mcp:EnableTrayEndpoints=false " +
            "(env Mcp__EnableTrayEndpoints=false). Nothing consumes /tray/* off the local machine — the tray " +
            "is a macOS client that talks to 127.0.0.1. The published image already bakes this.\n" +
            "  * Local macOS / launchd install: set Mcp:BindAddress=127.0.0.1 and remove non-loopback " +
            "Mcp:AllowedHosts entries.\n" +
            "See docs/security.md \"/up, /health and /tray/*\".";
    }

    /// <summary>
    /// Unparseable addresses count as non-loopback. Program.cs rejects those
    /// separately and earlier, so this is only reached defensively — and
    /// "couldn't tell" must not resolve to "safe" in a guard like this one.
    ///
    /// <para>Shared with the origin-authentication warning in Program.cs, which
    /// asks the same question this guard does ("is this server reachable from
    /// off-host?") about a different risk. One implementation so the two can't
    /// disagree about what counts as exposed.</para>
    /// </summary>
    public static bool IsLoopbackBind(string? bindAddress) =>
        IPAddress.TryParse(bindAddress, out var ip) && IPAddress.IsLoopback(ip);
}
