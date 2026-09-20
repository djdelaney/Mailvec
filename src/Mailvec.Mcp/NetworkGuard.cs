using System.Net;

namespace Mailvec.Mcp;

/// <summary>
/// Refuses requests from networks the server should never be serving —
/// today, the compose <c>parse</c> network.
///
/// <para><b>Why it exists.</b> mcp joins the <c>parse</c> network so that
/// <c>view_attachment</c> and <c>get_attachment_page_image</c> can reach the
/// parse service. Docker networks are symmetric: membership that lets mcp
/// call parse also lets parse call mcp, and mcp binds <c>0.0.0.0</c>. The
/// parse service is the one process that eats attacker-chosen bytes, so a
/// compromise there could call <c>search_emails</c> and read the whole
/// mailbox — exactly what putting the parsers in a data-less container was
/// meant to prevent. <see cref="HostGuard"/> is not a defence here (the
/// caller controls its <c>Host</c> header), and origin authentication
/// (<c>Mcp:Access</c>) is off by default. Pinning the parse subnet in compose
/// and denying it here closes the return path in the default posture; Access
/// validation, where configured, is the stronger layer on top.</para>
///
/// <para><b>Loopback is never denied</b>, whatever the list says: the compose
/// healthcheck and <c>mailvec doctor</c> arrive on it, and a misconfigured
/// range must not be able to mark the container unhealthy. A null remote
/// address is not denied either — a deny-list narrows, it does not assert;
/// the allow-list controls (<c>/health</c>, the Access loopback exemption)
/// are the ones that fail closed on "couldn't tell".</para>
/// </summary>
public static class NetworkGuard
{
    /// <summary>Parse the configured CIDRs. A malformed entry is fatal at startup, never silently skipped.</summary>
    public static IReadOnlyList<IPNetwork> Parse(IEnumerable<string>? cidrs)
    {
        var list = new List<IPNetwork>();
        foreach (var raw in cidrs ?? [])
        {
            var cidr = raw?.Trim();
            if (string.IsNullOrEmpty(cidr)) continue;
            if (!IPNetwork.TryParse(cidr, out var network))
                throw new InvalidOperationException($"Mcp:DeniedNetworks entry '{raw}' is not a CIDR network (e.g. 172.31.255.0/24).");
            list.Add(network);
        }
        return list;
    }

    public static bool IsDenied(IPAddress? remote, IReadOnlyList<IPNetwork> denied)
    {
        if (remote is null || denied.Count == 0) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote)) return false;
        foreach (var network in denied)
        {
            if (network.Contains(remote)) return true;
        }
        return false;
    }
}
