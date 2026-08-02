using Mailvec.Core.Options;
using Mailvec.Mcp;

namespace Mailvec.Mcp.Tests;

/// <summary>
/// The matrix for the startup guard that makes "unauthenticated mail-bearing
/// surface, reachable off-host" unrepresentable rather than merely
/// non-default. Both real deployments are pinned here as safe, so a future
/// tightening that breaks the launchd install or the container fails loudly.
/// </summary>
public class TrayExposureGuardTests
{
    private static McpOptions Opts(
        bool tray = true,
        string bind = "127.0.0.1",
        string[]? allowedHosts = null) =>
        new() { EnableTrayEndpoints = tray, BindAddress = bind, AllowedHosts = allowedHosts ?? [] };

    // ---------- the two shapes that must keep working ----------

    [Fact]
    public void Loopback_launchd_install_is_allowed()
    {
        // Bound to loopback, no fronting hostname, tray on: the macOS install
        // the tray actually exists for.
        TrayExposureGuard.Violation(Opts()).ShouldBeNull();
    }

    [Fact]
    public void Container_config_is_allowed_because_tray_is_off()
    {
        // Exactly what the published image + compose produce: 0.0.0.0 bind and
        // a public hostname, but tray disabled. Every check must be gated on
        // EnableTrayEndpoints — this is the live deployment.
        TrayExposureGuard.Violation(
            Opts(tray: false, bind: "0.0.0.0", allowedHosts: ["mailvec.example.com", "mcp"]))
            .ShouldBeNull();
    }

    // ---------- the shapes that must be refused ----------

    [Fact]
    public void Non_loopback_bind_with_tray_on_is_refused_even_with_no_allowed_hosts()
    {
        // The case that keying this guard off "is a public hostname configured"
        // would MISS entirely, and the reason the bind address is the primary
        // signal: HostGuard always admits Host: localhost, so an empty
        // AllowedHosts does not make a 0.0.0.0 bind unreachable. Anything that
        // can route to the port reads mail.
        var violation = TrayExposureGuard.Violation(Opts(bind: "0.0.0.0"));

        violation.ShouldNotBeNull();
        violation.ShouldContain("0.0.0.0");
        violation.ShouldContain("Mcp:EnableTrayEndpoints=false");
    }

    [Fact]
    public void Fronting_hostname_with_tray_on_is_refused_even_on_a_loopback_bind()
    {
        // A non-loopback allowed host means something fronts this server under
        // a real name. On a loopback bind that front is same-host (local
        // reverse proxy, or cloudflared sharing the network namespace) and
        // reaches /tray/* just as well.
        var violation = TrayExposureGuard.Violation(Opts(allowedHosts: ["mailvec.example.com"]));

        violation.ShouldNotBeNull();
        violation.ShouldContain("mailvec.example.com");
    }

    [Fact]
    public void Both_signals_are_reported_together()
    {
        // One restart per problem is a bad way to learn your config; the
        // message names everything wrong at once.
        var violation = TrayExposureGuard.Violation(
            Opts(bind: "0.0.0.0", allowedHosts: ["mailvec.example.com"]));

        violation.ShouldNotBeNull();
        violation.ShouldContain("0.0.0.0");
        violation.ShouldContain("mailvec.example.com");
    }

    // ---------- classification details ----------

    [Fact]
    public void Loopback_names_in_allowed_hosts_are_not_treated_as_fronting()
    {
        // Redundant but harmless: these are already always-allowed by
        // HostGuard, so listing them signals nothing about exposure.
        TrayExposureGuard.Violation(Opts(allowedHosts: ["localhost", "127.0.0.1", "::1"]))
            .ShouldBeNull();
    }

    [Fact]
    public void Empty_and_whitespace_allowed_hosts_entries_are_ignored()
    {
        // compose wires Mcp__AllowedHosts__0/__2 from optionally-unset env
        // vars, so empty entries are the NORMAL case, not a malformed one.
        // Treating them as fronting hostnames would refuse to start every
        // loopback deployment that happens to use the compose file's shape.
        TrayExposureGuard.Violation(Opts(allowedHosts: ["", "   "])).ShouldBeNull();
    }

    [Fact]
    public void Ipv6_loopback_bind_is_allowed()
    {
        TrayExposureGuard.Violation(Opts(bind: "::1")).ShouldBeNull();
    }

    [Fact]
    public void Unparseable_bind_address_is_treated_as_exposed()
    {
        // Program.cs rejects these earlier with its own named error, so this is
        // defensive — but "couldn't tell" must never resolve to "safe" here.
        TrayExposureGuard.Violation(Opts(bind: "localhost")).ShouldNotBeNull();
    }

    [Fact]
    public void A_disabled_tray_is_never_a_violation_whatever_else_is_set()
    {
        TrayExposureGuard.Violation(Opts(tray: false, bind: "0.0.0.0")).ShouldBeNull();
        TrayExposureGuard.Violation(Opts(tray: false, allowedHosts: ["x.example.com"])).ShouldBeNull();
        TrayExposureGuard.Violation(Opts(tray: false, bind: "bogus")).ShouldBeNull();
    }
}
