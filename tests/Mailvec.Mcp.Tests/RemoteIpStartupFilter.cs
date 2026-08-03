using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Mailvec.Mcp.Tests;

/// <summary>
/// Stamps a remote address onto every request at the very front of the
/// pipeline.
///
/// <para><b>Why the test host needs this.</b> <c>TestServer</c> synthesises
/// requests rather than accepting connections, so
/// <c>HttpContext.Connection.RemoteIpAddress</c> is null. Two controls read it
/// and both fail closed on null (deliberately — "couldn't tell where this came
/// from" must not resolve to "safe"): the <c>/health</c> loopback restriction
/// and the Cloudflare Access loopback exemption. Without a stamped address the
/// test host is neither loopback nor remote, which is a state no real
/// deployment is ever in — so tests would be exercising a fiction.</para>
///
/// <para>Factories therefore declare which caller they are simulating.
/// <see cref="IPAddress.Loopback"/> is the honest default for the local install
/// (its callers genuinely are loopback); an off-box address is what the
/// container's cloudflared and sibling containers look like.</para>
/// </summary>
internal sealed class RemoteIpStartupFilter(IPAddress remoteIp) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = remoteIp;
            await nextMiddleware();
        });
        next(app);
    };
}
