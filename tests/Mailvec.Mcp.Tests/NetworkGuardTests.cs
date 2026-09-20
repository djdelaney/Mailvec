using System.Net;
using Mailvec.Mcp;

namespace Mailvec.Mcp.Tests;

public class NetworkGuardTests
{
    private static IReadOnlyList<IPNetwork> Parse(params string[] cidrs) => NetworkGuard.Parse(cidrs);

    [Theory]
    [InlineData("172.31.255.2")]
    [InlineData("172.31.255.254")]
    [InlineData("::ffff:172.31.255.9")] // IPv4-mapped, as Kestrel reports dual-stack peers
    public void An_address_inside_a_denied_network_is_denied(string ip)
    {
        NetworkGuard.IsDenied(IPAddress.Parse(ip), Parse("172.31.255.0/24")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("172.31.254.9")]  // neighbouring /24
    [InlineData("172.18.0.7")]    // the default network, where cloudflared lives
    [InlineData("10.0.0.1")]
    public void An_address_outside_every_denied_network_is_allowed(string ip)
    {
        NetworkGuard.IsDenied(IPAddress.Parse(ip), Parse("172.31.255.0/24")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Loopback_is_never_denied_whatever_the_list_says(string ip)
    {
        // The compose healthcheck and `mailvec doctor` arrive on loopback; a
        // misconfigured range must not be able to mark the container unhealthy.
        NetworkGuard.IsDenied(IPAddress.Parse(ip), Parse("0.0.0.0/0", "::/0")).ShouldBeFalse();
    }

    [Fact]
    public void A_null_remote_address_is_not_denied()
    {
        // A deny-list narrows; it does not assert. The allow-list controls
        // are the ones that fail closed on "couldn't tell".
        NetworkGuard.IsDenied(null, Parse("172.31.255.0/24")).ShouldBeFalse();
    }

    [Fact]
    public void An_empty_list_denies_nothing()
    {
        NetworkGuard.IsDenied(IPAddress.Parse("172.31.255.2"), Parse()).ShouldBeFalse();
        NetworkGuard.Parse(null).ShouldBeEmpty();
        NetworkGuard.Parse([" ", ""]).ShouldBeEmpty();
    }

    [Fact]
    public void A_malformed_entry_is_fatal_not_skipped()
    {
        // Silently dropping a bad CIDR would leave the parse network served
        // with a config that looks like it denies it.
        Should.Throw<InvalidOperationException>(() => NetworkGuard.Parse(["172.31.255.0/24", "parse-network"]))
            .Message.ShouldContain("parse-network");
    }
}
