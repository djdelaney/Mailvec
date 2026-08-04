using System.Net;
using Mailvec.Core.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Mailvec.Mcp;

/// <summary>
/// Validates Cloudflare Access's <c>Cf-Access-Jwt-Assertion</c> at the origin.
/// See <see cref="AccessOptions"/> for what this buys and why it's opt-in.
///
/// <para><b>Everything here reads its configuration lazily, from DI.</b> The
/// scheme, policies and handler are registered unconditionally at builder time,
/// but not one of them looks at a value until a request is being evaluated —
/// which is after <c>Build()</c>, and therefore after the options pipeline has
/// applied env vars and every other source. This is the same reason
/// <c>Program.cs</c> reads <c>EnableTrayEndpoints</c> from
/// <c>IOptions&lt;McpOptions&gt;</c> rather than the builder-time snapshot: a
/// builder-time <c>Configuration.Get&lt;McpOptions&gt;()</c> misses overrides
/// applied later, so a security control keyed off one can be silently off.</para>
///
/// <para>Registering the scheme unconditionally is inert on its own: with
/// <c>Mcp:Access:Enabled</c> false, <c>Program.cs</c> adds no authentication
/// middleware and puts no policy on any endpoint, so none of this is ever
/// invoked. That keeps the loopback install's pipeline byte-for-byte what it
/// was.</para>
/// </summary>
internal static class AccessAuth
{
    /// <summary>
    /// The header Cloudflare Access adds to every request it admits. Note this
    /// is NOT <c>Authorization</c>: on a claude.ai connector request that header
    /// carries the connector's own OAuth access token, a different credential
    /// with a different issuer. Reading the wrong one would authenticate the
    /// wrong thing, so the fallback is explicitly suppressed below.
    /// </summary>
    internal const string AssertionHeader = "Cf-Access-Jwt-Assertion";

    /// <summary>Everything mail-bearing: the MCP endpoint and <c>/health</c>.</summary>
    internal const string OwnerPolicy = "mailvec-access-owner";

    /// <summary><c>/up</c> only — additionally admits the path-scoped monitoring app's audience.</summary>
    internal const string MonitoringPolicy = "mailvec-access-monitoring";

    internal static void AddAccessAuthentication(IServiceCollection services)
    {
        // The loopback bypass needs the connection, which isn't reachable from
        // AuthorizationHandlerContext.
        services.AddHttpContextAccessor();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured from IOptions<McpOptions>, so the values are the resolved
        // ones. Runs on first use, which is the first authenticated request.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<McpOptions>>((options, mcp) =>
            {
                var access = mcp.Value.Access;
                if (!access.Enabled)
                {
                    // Never reached — no middleware is wired when disabled — but
                    // leave the options in a shape the framework's own
                    // post-configure won't throw on if something resolves them.
                    options.RequireHttpsMetadata = false;
                    return;
                }

                // JWKS. Built here rather than left to the framework's
                // MetadataAddress path, which assumes an OIDC discovery document
                // Cloudflare Access does not publish — see AccessCertsRetriever.
                // ConfigurationManager still owns the caching, the bounded
                // refresh, and the negative-result backoff that stops a dead
                // endpoint becoming a request-rate hammer.
                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    access.CertsAddress,
                    new AccessCertsRetriever(access.TeamDomain.TrimEnd('/')),
                    // Key material over plaintext would defeat the exercise. The
                    // scheme is also enforced ahead of here by
                    // AccessOptions.Validate(); this is the belt to that braces,
                    // and the one that binds the actual fetch.
                    new HttpDocumentRetriever { RequireHttps = true });
                options.RequireHttpsMetadata = true;
                // Keep claim names as they arrive ("aud", "email", "sub")
                // instead of the legacy SOAP-ish URIs the inbound mapper
                // rewrites them to — the audience check below reads "aud".
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = access.TeamDomain.TrimEnd('/'),
                    // Scheme-level audience check is the coarse one: it rejects
                    // tokens for applications outside this deployment entirely.
                    // Per-endpoint policy then narrows to which of OUR apps.
                    ValidateAudience = true,
                    ValidAudiences = access.AllAudiences(),
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    // Tighter than the 5-minute default. Access assertions are
                    // minted seconds before use by an edge whose clock is not
                    // ours to doubt, and a long skew extends the life of a
                    // replayed expired token for no operational gain.
                    ClockSkew = TimeSpan.FromSeconds(60),
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var assertion = ctx.Request.Headers[AssertionHeader].ToString();
                        if (string.IsNullOrWhiteSpace(assertion))
                        {
                            // NoResult, not "leave Token null". A null Token
                            // makes the handler fall back to parsing the
                            // Authorization header, which on a connector
                            // request holds a completely different token. This
                            // line is what keeps that from authenticating.
                            ctx.NoResult();
                            return Task.CompletedTask;
                        }
                        ctx.Token = assertion;
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization(options =>
        {
            // The requirements carry a SCOPE, not a resolved audience list —
            // the list comes from options at evaluation time, for the same
            // laziness reason as everything else here.
            options.AddPolicy(OwnerPolicy, p =>
                p.AddRequirements(new AccessAudienceRequirement(AccessScope.Owner)));
            options.AddPolicy(MonitoringPolicy, p =>
                p.AddRequirements(new AccessAudienceRequirement(AccessScope.Monitoring)));
        });

        services.AddSingleton<IAuthorizationHandler, AccessAudienceHandler>();
    }

    /// <summary>
    /// Fetch the signing keys once at startup, log the URL and the key ids, and
    /// refuse to start if no keys come back.
    ///
    /// <para><b>This exists because the alternative is a silent total outage.</b>
    /// Key retrieval is otherwise lazy — first request — and a failure there is
    /// swallowed by <c>JsonWebTokenHandler</c> into an EventSource no logger is
    /// listening to, leaving a server that logs "validation ENABLED", passes its
    /// healthcheck (loopback is exempt), and 401s every real caller. That is
    /// exactly how the broken discovery URL shipped: every negative test passed,
    /// because a server that authenticates nobody refuses bad tokens perfectly.
    /// A boot that names the knob is worth a boot that can fail.</para>
    ///
    /// <para>Bounded, because a hang at startup is worse than a refusal: an
    /// unreachable Cloudflare must not wedge the container short of its restart
    /// policy. Same reasoning as <c>OllamaClient.PingAsync</c>'s linked CTS.</para>
    /// </summary>
    internal static async Task VerifySigningKeysAsync(
        IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        var access = services.GetRequiredService<IOptions<McpOptions>>().Value.Access;
        var options = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        if (options.ConfigurationManager is not BaseConfigurationManager manager)
        {
            throw new InvalidOperationException(
                "Mcp:Access:Enabled is true but no signing-key source is configured. "
                + "Every request would fail signature validation with IDX10500.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        BaseConfiguration configuration;
        try
        {
            configuration = await manager.GetBaseConfigurationAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Wrapped, not rethrown bare: the inner IDX208xx names a URL and a
            // status code but not the setting that produced them.
            throw new InvalidOperationException(
                $"Could not retrieve the Cloudflare Access signing keys from '{access.CertsAddress}'. "
                + "Mailvec would authenticate nobody, so it will not start. Check Mcp:Access:TeamDomain "
                + "and that the origin can reach your Zero Trust team domain.", ex);
        }

        // Belt to the retriever's own emptiness check — that one guards the
        // refresh path, this one guards a ConfigurationManager swapped in from
        // elsewhere. Neither is redundant with the other.
        if (configuration.SigningKeys.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cloudflare Access returned no signing keys from '{access.CertsAddress}'. "
                + "Every assertion would fail signature validation, so Mailvec will not start.");
        }

        logger.LogInformation(
            "Cloudflare Access signing keys loaded from {CertsUrl}: {KeyCount} key(s), kid {KeyIds}.",
            access.CertsAddress,
            configuration.SigningKeys.Count,
            string.Join(", ", configuration.SigningKeys.Select(k => k.KeyId)));
    }
}

/// <summary>Which set of audiences an endpoint accepts.</summary>
internal enum AccessScope
{
    /// <summary>The mailbox application only.</summary>
    Owner,

    /// <summary>The mailbox application, plus the path-scoped monitoring app.</summary>
    Monitoring,
}

/// <summary>
/// Requires the validated assertion's <c>aud</c> to fall in the endpoint's
/// scope. Separate from the scheme-level audience check because the two answer
/// different questions: the scheme asks "is this a token for this deployment",
/// the policy asks "for THIS endpoint".
/// </summary>
internal sealed class AccessAudienceRequirement(AccessScope scope) : IAuthorizationRequirement
{
    public AccessScope Scope { get; } = scope;
}

internal sealed class AccessAudienceHandler(
    IHttpContextAccessor http,
    IOptions<McpOptions> mcp,
    ILogger<AccessAudienceHandler> logger)
    : AuthorizationHandler<AccessAudienceRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AccessAudienceRequirement requirement)
    {
        var access = mcp.Value.Access;

        if (access.AllowLoopback && IsLoopback(http.HttpContext))
        {
            // The compose healthcheck and `mailvec doctor` reach /health this
            // way and have no assertion to present. See AccessOptions.AllowLoopback
            // for why loopback is not a hole in the container.
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            var permitted = requirement.Scope == AccessScope.Monitoring
                ? access.MonitoringAudiences()
                : access.OwnerAudiences();

            var presented = context.User.FindAll("aud").Select(c => c.Value);
            if (presented.Any(a => permitted.Contains(a, StringComparer.Ordinal)))
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }

            // The interesting denial, and the one worth a log line: a caller
            // holding a genuinely valid assertion for a DIFFERENT application —
            // in practice the monitoring token reaching for the mailbox. No
            // token, no claim values, no mail content; the audience is an
            // opaque Access app id, not a secret.
            logger.LogWarning(
                "Access assertion rejected for {Path}: audience not permitted on this endpoint.",
                http.HttpContext?.Request.Path.Value);
        }

        // Never Fail() — only decline. Fail() is a veto that other handlers
        // can't override, which would make this unusable if a second policy is
        // ever composed alongside it. Declining is already deny-by-default.
        return Task.CompletedTask;
    }

    private static bool IsLoopback(HttpContext? context)
    {
        var remote = context?.Connection.RemoteIpAddress;
        // Null means we can't tell where it came from (no real connection).
        // Treat unknown as not-loopback: the whole point is failing closed.
        return remote is not null && IPAddress.IsLoopback(remote);
    }
}
