namespace Mailvec.Core.Options;

/// <summary>
/// Origin-side validation of Cloudflare Access's <c>Cf-Access-Jwt-Assertion</c>
/// header (config section <c>Mcp:Access</c>).
///
/// <para><b>What this changes about the trust model.</b> Without it the origin
/// authenticates nobody: anything that can reach <c>mcp:3333</c> can call every
/// tool, and the entire boundary is a Cloudflare Access policy configured in
/// Cloudflare's control plane — outside this repo, unversioned, and verifiable
/// only by remembering to go and look. With it, a request that didn't come
/// through Access carries no assertion and is refused by the server itself. The
/// tunnel remains the only ingress; this is the second layer, not a replacement
/// for the first.</para>
///
/// <para><b>It also moves the /up split from a promise into code.</b>
/// docs/security.md says the monitoring service token must reach <c>/up</c> and
/// nothing else, and that this "lives in Cloudflare's control plane rather than
/// in this repo — so it is a requirement to verify, never a property to assume".
/// <see cref="MonitoringAudience"/> is that assumption made checkable at the
/// origin: a token minted for the path-scoped monitoring app fails the audience
/// check on <c>/</c> and <c>/health</c> even if the Access policy is wrong.</para>
///
/// <para><b>Why <see cref="Enabled"/> defaults to false.</b> Mailvec ships two
/// deployment shapes and only one of them has Cloudflare in front of it. The
/// loopback/launchd install (<c>ops/install-all.sh</c>) has no team domain, no
/// Access application, and no assertion on any request — defaulting this on
/// would break it at startup for everyone running that shape. Fail-closed here
/// means <i>"once configured, never silently degrade to allowing"</i>, and that
/// is what the rest of this class enforces: an <see cref="Enabled"/> with
/// missing settings refuses to start rather than half-validating, and an
/// unreachable JWKS endpoint produces 401s rather than a fallback to open.</para>
/// </summary>
public sealed class AccessOptions
{
    /// <summary>
    /// Master switch. When false, no authentication middleware is registered at
    /// all and the HTTP surface behaves exactly as it did before this existed —
    /// deliberately, so the loopback install has no new failure mode.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The Zero Trust team domain, e.g. <c>https://myteam.cloudflareaccess.com</c>.
    /// Serves as both the expected <c>iss</c> and the base for <see cref="CertsAddress"/>,
    /// from which the signing keys are fetched and refreshed. Must be absolute
    /// and HTTPS — key material fetched over plaintext would defeat the whole
    /// exercise. Give it WITH the scheme: it is compared verbatim against the
    /// <c>iss</c> claim, which Cloudflare mints as the full <c>https://</c> URL.
    /// </summary>
    public string TeamDomain { get; set; } = "";

    /// <summary>
    /// The Application Audience (AUD) tag of the Access application fronting the
    /// mailbox surface. Required when <see cref="Enabled"/>. This is the claim
    /// that distinguishes "a valid token from our Access account" from "a valid
    /// token for THIS application" — without it, any assertion the same team
    /// issues for any unrelated app would authenticate here.
    /// </summary>
    public string Audience { get; set; } = "";

    /// <summary>
    /// AUD tag of the separate path-scoped Access application that fronts
    /// <c>/up</c> for the external monitor. Optional. When set, tokens carrying
    /// it are accepted on <c>/up</c> only; when unset, <c>/up</c> requires the
    /// same audience as everything else. Never grants <c>/</c> or <c>/health</c>
    /// — that asymmetry is the entire point of having a second app.
    /// </summary>
    public string MonitoringAudience { get; set; } = "";

    /// <summary>
    /// Exempt requests arriving over the loopback interface. Default true, and
    /// load-bearing for the container: the compose healthcheck curls
    /// <c>127.0.0.1:3333/health</c> from inside the mcp container, and
    /// <c>mailvec doctor</c>'s HTTP probe does the same under
    /// <c>docker compose exec</c>. Neither has an assertion to present, so
    /// without this the container reports itself permanently unhealthy the
    /// moment Access validation is switched on.
    ///
    /// <para>Safe because loopback is not reachable from off-box: cloudflared
    /// and every sibling container connect to <c>mcp:3333</c> over the compose
    /// network, so their requests arrive with the container's network address,
    /// not 127.0.0.1. Anything already able to originate from the container's
    /// own loopback is inside the process's blast radius regardless.</para>
    /// </summary>
    public bool AllowLoopback { get; set; } = true;

    /// <summary>
    /// The JWKS Cloudflare Access signs its assertions with — a bare key set,
    /// NOT an OIDC discovery document.
    ///
    /// <para><b>Cloudflare Access publishes no discovery document at the team
    /// domain.</b> This used to derive
    /// <c>/cdn-cgi/access/.well-known/openid-configuration</c> and hand it to
    /// <c>JwtBearerOptions.MetadataAddress</c>, which 404s on every team domain
    /// — verified against four independent ones. The 404 was invisible:
    /// <c>JsonWebTokenHandler</c> catches a metadata-retrieval failure, logs
    /// IDX10261 to <c>IdentityModelEventSource</c> (an EventSource, so Serilog
    /// never sees it) and proceeds with zero keys, so every request 401'd with
    /// IDX10500 while the origin logged no retrieval attempt at all and the
    /// container stayed green. Fetch the key set directly instead — see
    /// <c>AccessCertsRetriever</c>, and <c>AccessAuth.VerifySigningKeysAsync</c>
    /// for the boot-time fetch that makes a future breakage loud.</para>
    /// </summary>
    public string CertsAddress => $"{TeamDomain.TrimEnd('/')}/cdn-cgi/access/certs";

    /// <summary>
    /// Every audience the server will accept a token for, in any position.
    /// Per-endpoint policy narrows this further — passing the scheme-level check
    /// is necessary, not sufficient.
    /// </summary>
    public string[] AllAudiences() =>
        string.IsNullOrWhiteSpace(MonitoringAudience)
            ? [Audience]
            : [Audience, MonitoringAudience];

    /// <summary>
    /// Audiences accepted on <c>/up</c>: the owner's, plus the monitoring app's
    /// when one is configured.
    /// </summary>
    public string[] MonitoringAudiences() => AllAudiences();

    /// <summary>
    /// Audiences accepted everywhere else — the owner's application only.
    /// </summary>
    public string[] OwnerAudiences() => [Audience];

    /// <summary>
    /// Configuration error message, or null when the settings are coherent.
    /// Checked at startup so a misconfiguration is a refusal to boot rather than
    /// a server that looks protected and isn't. Only ever fires when someone has
    /// explicitly opted in, so it can't break the default shape.
    /// </summary>
    public string? Validate()
    {
        if (!Enabled) return null;

        if (string.IsNullOrWhiteSpace(TeamDomain))
            return "Mcp:Access:Enabled is true but Mcp:Access:TeamDomain is empty. "
                 + "Set it to your Zero Trust team domain, e.g. https://myteam.cloudflareaccess.com.";

        if (!Uri.TryCreate(TeamDomain, UriKind.Absolute, out var uri))
            return $"Mcp:Access:TeamDomain '{TeamDomain}' is not an absolute URL. "
                 + "Expected e.g. https://myteam.cloudflareaccess.com.";

        // Signing keys are fetched from this origin, so plaintext would let a
        // network position substitute the keys that authenticate every caller.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return $"Mcp:Access:TeamDomain '{TeamDomain}' must use https — the Access signing keys are fetched from it.";

        if (string.IsNullOrWhiteSpace(Audience))
            return "Mcp:Access:Enabled is true but Mcp:Access:Audience is empty. "
                 + "Set it to the Application Audience (AUD) tag of the Access application in front of Mailvec. "
                 + "Without it any assertion your team issues for any application would be accepted here.";

        // A monitoring audience equal to the owner's grants the monitor the whole
        // mailbox while reading like a restriction — the exact failure docs/security.md
        // warns about under "Any Access Service Token", just spelled differently.
        if (!string.IsNullOrWhiteSpace(MonitoringAudience)
            && string.Equals(MonitoringAudience, Audience, StringComparison.Ordinal))
        {
            return "Mcp:Access:MonitoringAudience is set to the same value as Mcp:Access:Audience. "
                 + "That gives the monitoring credential the full mailbox surface. Use a separate, "
                 + "path-scoped Access application for /up, or leave MonitoringAudience empty.";
        }

        return null;
    }
}
