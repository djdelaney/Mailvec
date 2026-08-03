using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mailvec.Core.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Mailvec.Mcp.Tests;

/// <summary>
/// Origin-side validation of Cloudflare Access's <c>Cf-Access-Jwt-Assertion</c>.
///
/// <para><b>What these are really testing.</b> Before this existed the origin
/// authenticated nobody and the whole boundary was a Cloudflare Access policy
/// living in Cloudflare's control plane — unversioned, and verifiable only by
/// remembering to go and look at it. Every assertion below is a property that
/// used to be a promise. In particular the monitoring-audience cases: they prove
/// at the ORIGIN that a leaked monitoring credential can't read mail, which
/// docs/security.md previously had to describe as "a requirement to verify,
/// never a property to assume".</para>
///
/// <para>Signing keys are injected via <see cref="StaticConfigurationManager{T}"/>
/// so no test touches the network — the JWKS fetch is the framework's job, and
/// stubbing it here would test the framework rather than our configuration of it.
/// What we do own and do test: which header the token is read from, which
/// audience is accepted where, and what happens when validation can't succeed.</para>
/// </summary>
public class AccessAuthTests
{
    private const string TeamDomain = "https://mailvec-test.cloudflareaccess.com";
    private const string OwnerAud = "owner-application-aud-tag";
    private const string MonitorAud = "monitoring-application-aud-tag";

    /// <summary>
    /// A JSON-RPC tools/list request shaped the way the Streamable HTTP
    /// transport requires: a POST (a GET would 405 before authorization ever
    /// ran, passing for the wrong reason) carrying the dual Accept header the
    /// transport insists on — without it the endpoint answers 406 and an
    /// "authorized" assertion looks like a failure.
    /// </summary>
    private static HttpRequestMessage ToolsListRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    // ---------- no credential ----------

    [Theory]
    [InlineData("/health")]
    [InlineData("/up")]
    public async Task Unauthenticated_request_is_refused_before_the_handler_runs(string path)
    {
        using var factory = new AccessFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unauthenticated_mcp_call_is_refused_before_dispatch()
    {
        using var factory = new AccessFactory();
        using var client = factory.CreateClient();

        using var request = ToolsListRequest();
        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // Nothing resembling a tool list came back — the refusal is before
        // dispatch, not a 200 body with an error inside it.
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("search_emails");
    }

    // ---------- invalid credentials ----------

    [Fact]
    public async Task Expired_assertion_is_refused()
    {
        using var factory = new AccessFactory();
        var token = factory.Token(OwnerAud, expires: DateTime.UtcNow.AddHours(-2), notBefore: DateTime.UtcNow.AddHours(-3));

        (await factory.GetWithAssertion("/health", token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Not_yet_valid_assertion_is_refused()
    {
        using var factory = new AccessFactory();
        var token = factory.Token(OwnerAud, notBefore: DateTime.UtcNow.AddHours(2), expires: DateTime.UtcNow.AddHours(3));

        (await factory.GetWithAssertion("/health", token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_issuer_is_refused()
    {
        using var factory = new AccessFactory();
        var token = factory.Token(OwnerAud, issuer: "https://someone-elses-team.cloudflareaccess.com");

        (await factory.GetWithAssertion("/health", token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Assertion_for_an_unrelated_application_is_refused()
    {
        // Same team, valid signature, real user — but minted for a different
        // Access application. Without the audience check this authenticates,
        // which is why AccessOptions requires one.
        using var factory = new AccessFactory();
        var token = factory.Token(audience: "some-other-app-in-the-same-account");

        (await factory.GetWithAssertion("/health", token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Assertion_signed_by_an_unknown_key_is_refused()
    {
        using var factory = new AccessFactory();
        using var attacker = RSA.Create(2048);
        var token = factory.Token(OwnerAud, signingKey: new RsaSecurityKey(attacker) { KeyId = "attacker-key" });

        (await factory.GetWithAssertion("/health", token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unsigned_alg_none_assertion_is_refused()
    {
        // The classic JWT bypass: correct claims, `alg: none`, empty signature.
        using var factory = new AccessFactory();
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64("""{"alg":"none","typ":"JWT"}""");
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var payload = B64($$"""{"aud":"{{OwnerAud}}","iss":"{{TeamDomain}}","exp":{{exp}},"email":"attacker@example.com"}""");

        (await factory.GetWithAssertion("/health", $"{header}.{payload}.")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Malformed_assertion_is_refused_without_a_server_error()
    {
        using var factory = new AccessFactory();

        // A 500 here would mean an unhandled parse exception — a remote,
        // unauthenticated crash-the-handler path, which is worse than the auth
        // gap it sits in front of.
        (await factory.GetWithAssertion("/health", "not-a-jwt-at-all")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---------- credentials that look like credentials but aren't ----------

    [Fact]
    public async Task A_spoofed_identity_header_alone_never_authenticates()
    {
        // Cf-Access-Authenticated-User-Email is trivially forgeable by anything
        // that can reach the origin. It is only meaningful when covered by a
        // validated assertion, and nothing in the pipeline may read it on its
        // own. Pinned because "just read the email header" is the tempting
        // shortcut this whole file exists to prevent.
        using var factory = new AccessFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cf-Access-Authenticated-User-Email", "owner@example.com");

        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_valid_token_in_the_Authorization_header_does_not_authenticate()
    {
        // On a claude.ai connector request, Authorization carries the
        // connector's OWN OAuth access token — a different credential from a
        // different issuer. JwtBearer falls back to that header unless the
        // handler explicitly stops, so this pins the NoResult() in
        // AccessAuth.OnMessageReceived. Same token, right place = accepted
        // (asserted below); wrong place = refused.
        using var factory = new AccessFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Token(OwnerAud));

        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---------- valid credentials ----------

    [Fact]
    public async Task Valid_owner_assertion_is_admitted()
    {
        using var factory = new AccessFactory();

        var response = await factory.GetWithAssertion("/health", factory.Token(OwnerAud));

        // 503, not 200: the fixture runs no Ollama, so /health reports degraded.
        // The point is that it reached the handler at all.
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).ShouldContain("status");
    }

    [Fact]
    public async Task Valid_owner_assertion_reaches_the_mcp_tool_surface()
    {
        using var factory = new AccessFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AccessAuth.AssertionHeader, factory.Token(OwnerAud));

        using var request = ToolsListRequest();
        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("search_emails");
    }

    // ---------- the /up split, enforced at the origin ----------

    [Fact]
    public async Task Monitoring_assertion_reaches_up()
    {
        using var factory = new AccessFactory();

        var response = await factory.GetWithAssertion("/up", factory.Token(MonitorAud));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable); // degraded, but served
        (await response.Content.ReadAsStringAsync()).ShouldContain("status");
    }

    [Theory]
    [InlineData("/health")]
    public async Task Monitoring_assertion_is_forbidden_on_mail_bearing_paths(string path)
    {
        // 403 rather than 401: the credential is genuine and validated, it just
        // isn't permitted here. This is the assertion that turns docs/security.md's
        // "verify the token is path-scoped in Cloudflare" into something the
        // origin enforces on its own.
        using var factory = new AccessFactory();

        var response = await factory.GetWithAssertion(path, factory.Token(MonitorAud));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Monitoring_assertion_is_forbidden_on_the_mcp_endpoint()
    {
        using var factory = new AccessFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AccessAuth.AssertionHeader, factory.Token(MonitorAud));

        using var request = ToolsListRequest();
        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("search_emails");
    }

    [Fact]
    public async Task Owner_assertion_also_reaches_up()
    {
        // The owner is not locked out of their own monitoring endpoint by the
        // narrower policy — /up admits both audiences, /health and / admit one.
        using var factory = new AccessFactory();

        var response = await factory.GetWithAssertion("/up", factory.Token(OwnerAud));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    // ---------- loopback bypass ----------

    [Fact]
    public async Task Loopback_callers_are_exempt_so_the_container_healthcheck_survives()
    {
        // The compose healthcheck curls 127.0.0.1:3333/health from inside the
        // mcp container and has no assertion to present. Without this exemption,
        // switching Access validation on marks the container permanently
        // unhealthy — a self-inflicted outage, not a security win.
        using var factory = new AccessFactory(remoteIp: IPAddress.Loopback);
        using var client = factory.CreateClient();

        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Loopback_exemption_can_be_turned_off()
    {
        using var factory = new AccessFactory(remoteIp: IPAddress.Loopback, allowLoopback: false);
        using var client = factory.CreateClient();

        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_non_loopback_caller_is_never_exempt()
    {
        // Guards the shape that matters in the container: cloudflared and every
        // sibling connect over the compose network, so they arrive with a real
        // address and must present an assertion like anyone else.
        using var factory = new AccessFactory(remoteIp: IPAddress.Parse("172.18.0.7"));
        using var client = factory.CreateClient();

        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---------- the default shape is untouched ----------

    [Fact]
    public async Task With_access_disabled_nothing_is_required()
    {
        // The loopback/launchd install has no Cloudflare in front of it. This
        // pins that enabling the feature elsewhere didn't quietly add a
        // credential requirement to that shape.
        using var factory = new MailvecMcpFactory();
        using var client = factory.CreateClient();

        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    // ---------- configuration coherence (pure) ----------

    [Fact]
    public void Disabled_options_never_complain()
    {
        new AccessOptions().Validate().ShouldBeNull();
        // Even nonsense settings are fine while it's off — the switch is what
        // makes them load-bearing.
        new AccessOptions { TeamDomain = "nonsense" }.Validate().ShouldBeNull();
    }

    [Fact]
    public void Enabled_without_a_team_domain_refuses()
    {
        var err = new AccessOptions { Enabled = true, Audience = OwnerAud }.Validate();
        err.ShouldNotBeNull();
        err.ShouldContain("TeamDomain");
    }

    [Fact]
    public void Enabled_without_an_audience_refuses()
    {
        // The dangerous half-configuration: signature and issuer validated, so
        // it looks like it's working, while every application in the account is
        // admitted.
        var err = new AccessOptions { Enabled = true, TeamDomain = TeamDomain }.Validate();
        err.ShouldNotBeNull();
        err.ShouldContain("Audience");
    }

    [Fact]
    public void Enabled_with_a_plaintext_team_domain_refuses()
    {
        var err = new AccessOptions
        {
            Enabled = true,
            TeamDomain = "http://mailvec-test.cloudflareaccess.com",
            Audience = OwnerAud,
        }.Validate();
        err.ShouldNotBeNull();
        err.ShouldContain("https");
    }

    [Fact]
    public void Monitoring_audience_equal_to_the_owner_audience_refuses()
    {
        // Reads like a restriction, grants the whole mailbox — the same trap
        // docs/security.md flags as "Any Access Service Token", spelled
        // differently.
        var err = new AccessOptions
        {
            Enabled = true,
            TeamDomain = TeamDomain,
            Audience = OwnerAud,
            MonitoringAudience = OwnerAud,
        }.Validate();
        err.ShouldNotBeNull();
        err.ShouldContain("same value");
    }

    [Fact]
    public void Up_admits_the_monitoring_audience_but_owner_paths_do_not()
    {
        var opts = new AccessOptions
        {
            Enabled = true,
            TeamDomain = TeamDomain,
            Audience = OwnerAud,
            MonitoringAudience = MonitorAud,
        };

        opts.Validate().ShouldBeNull();
        opts.OwnerAudiences().ShouldBe([OwnerAud]);
        opts.MonitoringAudiences().ShouldBe([OwnerAud, MonitorAud], ignoreOrder: true);
    }

    [Fact]
    public void Without_a_monitoring_audience_up_falls_back_to_the_owner_audience()
    {
        // Not "everyone allowed" — the narrower default, so an operator who
        // hasn't set up a second Access app doesn't accidentally widen /up.
        var opts = new AccessOptions { Enabled = true, TeamDomain = TeamDomain, Audience = OwnerAud };

        opts.MonitoringAudiences().ShouldBe([OwnerAud]);
        opts.AllAudiences().ShouldBe([OwnerAud]);
    }

    // ---------- fixture ----------

    /// <summary>
    /// The real server with <c>Mcp:Access</c> switched on, its signing keys
    /// replaced by a locally generated RSA key so no test needs the network.
    /// Optionally stamps a remote IP onto the connection, which TestServer
    /// otherwise leaves null — that's what makes the loopback bypass testable.
    /// </summary>
    private sealed class AccessFactory : WebApplicationFactory<Program>
    {
        private readonly string _tempDir;
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly IPAddress _remoteIp;
        private readonly bool _allowLoopback;
        private readonly RsaSecurityKey _key;

        /// <summary>
        /// Defaults to an off-box caller — the shape that actually reaches an
        /// Access-gated deployment (cloudflared forwarding from the tunnel).
        /// Tests that want the loopback path say so explicitly.
        /// </summary>
        public AccessFactory(IPAddress? remoteIp = null, bool allowLoopback = true)
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "mailvec-access-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _remoteIp = remoteIp ?? IPAddress.Parse("172.18.0.7");
            _allowLoopback = allowLoopback;
            _key = new RsaSecurityKey(_rsa) { KeyId = "mailvec-test-signing-key" };
        }

        /// <summary>Mint an assertion. Defaults are a valid one for the owner app.</summary>
        public string Token(
            string? audience = null,
            string? issuer = null,
            DateTime? expires = null,
            DateTime? notBefore = null,
            SecurityKey? signingKey = null)
        {
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = issuer ?? TeamDomain,
                Audience = audience ?? OwnerAud,
                NotBefore = notBefore ?? DateTime.UtcNow.AddMinutes(-1),
                Expires = expires ?? DateTime.UtcNow.AddHours(1),
                SigningCredentials = new SigningCredentials(
                    signingKey ?? _key, SecurityAlgorithms.RsaSha256),
                Claims = new Dictionary<string, object>
                {
                    ["email"] = "owner@example.com",
                    ["sub"] = "owner-subject-id",
                },
            };
            return new JsonWebTokenHandler().CreateToken(descriptor);
        }

        public async Task<HttpResponseMessage> GetWithAssertion(string path, string assertion)
        {
            using var client = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add(AccessAuth.AssertionHeader, assertion);
            return await client.SendAsync(request);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Archive:DatabasePath"] = Path.Combine(_tempDir, "archive.sqlite"),
                    ["Ollama:BaseUrl"] = "http://127.0.0.1:1",
                    ["Ollama:RequestTimeoutSeconds"] = "5",
                    ["Fastmail:AccountId"] = "",
                    ["Mcp:Access:Enabled"] = "true",
                    ["Mcp:Access:TeamDomain"] = TeamDomain,
                    ["Mcp:Access:Audience"] = OwnerAud,
                    ["Mcp:Access:MonitoringAudience"] = MonitorAud,
                    ["Mcp:Access:AllowLoopback"] = _allowLoopback ? "true" : "false",
                    // /health is this file's probe surface for auth behaviour,
                    // so its own loopback restriction is off here — otherwise
                    // every off-box case 404s before authorization is reached
                    // and the tests would pass without testing anything. The
                    // interaction between the two controls has its own test in
                    // ProgramHttpTests.
                    ["Mcp:RestrictHealthToLoopback"] = "false",
                }));

            builder.ConfigureTestServices(services =>
            {
                // Replace the discovery/JWKS fetch with a static configuration.
                // Registered after AddJwtBearer's own post-configure, so this
                // wins. Everything else about validation stays production code.
                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options =>
                    {
                        var configuration = new OpenIdConnectConfiguration { Issuer = TeamDomain };
                        configuration.SigningKeys.Add(_key);
                        options.ConfigurationManager =
                            new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    });

                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(_remoteIp));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            _rsa.Dispose();
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

}
