using Mailvec.Mcp;

namespace Mailvec.Mcp.Tests;

public class HostGuardTests
{
    private static HashSet<string> Default() => HostGuard.BuildAllowedHosts(null);

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]     // what HostString.Host gives for `Host: [::1]:3333`
    [InlineData("LOCALHOST")] // case-insensitive
    public void Loopback_hosts_are_allowed(string host)
    {
        HostGuard.IsAllowed(host, origin: null, Default()).ShouldBeTrue();
    }

    [Fact]
    public void The_ipv6_loopback_reaches_the_guard_in_brackets()
    {
        // The premise behind allowing "[::1]": this is the string the guard is
        // actually handed for a Host or Origin naming the IPv6 loopback.
        new Microsoft.AspNetCore.Http.HostString("[::1]:3333").Host.ShouldBe("[::1]");
        new Uri("http://[::1]:3333").Host.ShouldBe("[::1]");
        HostGuard.IsAllowed("[::1]", origin: "http://[::1]:3333", Default()).ShouldBeTrue();
    }

    [Theory]
    [InlineData("evil.com")]
    [InlineData("attacker.example")]
    public void Non_loopback_host_is_rejected(string host)
    {
        // The DNS-rebinding case: after rebind the browser still sends the
        // attacker's hostname in the Host header.
        HostGuard.IsAllowed(host, origin: null, Default()).ShouldBeFalse();
    }

    [Fact]
    public void Empty_or_missing_host_is_rejected()
    {
        HostGuard.IsAllowed("", origin: null, Default()).ShouldBeFalse();
        HostGuard.IsAllowed(null, origin: null, Default()).ShouldBeFalse();
    }

    [Fact]
    public void Allowed_host_but_cross_origin_is_rejected()
    {
        // A direct cross-origin POST: Host resolves to loopback but the browser
        // reveals the real initiator via Origin.
        HostGuard.IsAllowed("127.0.0.1", "http://evil.com", Default()).ShouldBeFalse();
    }

    [Fact]
    public void Allowed_host_with_loopback_origin_is_allowed()
    {
        HostGuard.IsAllowed("127.0.0.1", "http://localhost:3333", Default()).ShouldBeTrue();
        HostGuard.IsAllowed("localhost", "http://127.0.0.1", Default()).ShouldBeTrue();
    }

    [Fact]
    public void Malformed_origin_is_rejected()
    {
        HostGuard.IsAllowed("127.0.0.1", "not a url", Default()).ShouldBeFalse();
    }

    [Fact]
    public void Configured_hostname_is_allowed()
    {
        var set = HostGuard.BuildAllowedHosts(["mail.example.com"]);
        HostGuard.IsAllowed("mail.example.com", origin: null, set).ShouldBeTrue();
        HostGuard.IsAllowed("mail.example.com", "https://mail.example.com", set).ShouldBeTrue();
        HostGuard.IsAllowed("evil.com", origin: null, set).ShouldBeFalse();
    }
}
