using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Mailvec.Mcp;

/// <summary>
/// Reads Cloudflare Access's signing keys from <c>/cdn-cgi/access/certs</c> and
/// presents them as the <see cref="OpenIdConnectConfiguration"/> that
/// <c>ConfigurationManager</c> — and therefore the JWT handler — expects.
///
/// <para><b>Why a custom retriever rather than <c>MetadataAddress</c>.</b> The
/// framework's default path assumes OIDC discovery, and Cloudflare Access
/// publishes no discovery document at the team domain (see
/// <see cref="Mailvec.Core.Options.AccessOptions.CertsAddress"/> for what that
/// cost). What it does publish is a bare JWKS. Wrapping that in a configuration
/// object is the smallest adapter that keeps the parts worth keeping:
/// <c>ConfigurationManager</c>'s caching, its bounded refresh, and its
/// negative-result backoff — which is the actual reason to depend on it rather
/// than fetch the JWKS by hand on every request.</para>
/// </summary>
internal sealed class AccessCertsRetriever(string issuer)
    : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        var json = await retriever.GetDocumentAsync(address, cancel).ConfigureAwait(false);

        var configuration = new OpenIdConnectConfiguration { Issuer = issuer, JwksUri = address };
        foreach (var key in new JsonWebKeySet(json).GetSigningKeys())
            configuration.SigningKeys.Add(key);

        // THROW rather than return a keyless configuration. A configuration with
        // no keys validates nothing and fails every request with IDX10500 — the
        // exact silent shape this whole file exists to prevent. Throwing instead
        // means: at boot, VerifySigningKeysAsync refuses to start and names the
        // knob; on a later refresh, ConfigurationManager keeps the last good key
        // set (IDX20806) instead of degrading to "authenticates nobody" because
        // one refresh came back empty.
        if (configuration.SigningKeys.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cloudflare Access returned no usable signing keys from '{address}'. " +
                "Expected a JWKS with at least one RS256 signing key. Check that " +
                "Mcp:Access:TeamDomain names your Zero Trust team domain.");
        }

        return configuration;
    }
}
