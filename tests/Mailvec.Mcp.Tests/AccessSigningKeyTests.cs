using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Mailvec.Core.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Mailvec.Mcp.Tests;

/// <summary>
/// Where the signing keys come from — the one part of Access validation that
/// <see cref="AccessAuthTests"/> deliberately does not exercise.
///
/// <para><b>Why this file exists.</b> Those tests inject keys through a
/// <c>StaticConfigurationManager</c>, on the reasoning that the JWKS fetch is
/// the framework's job and stubbing it "would test the framework rather than
/// our configuration of it". That reasoning had a hole: the metadata URL <i>is</i>
/// our configuration, and it was wrong. v0.2.0 derived an OIDC discovery
/// document at <c>/cdn-cgi/access/.well-known/openid-configuration</c>, which
/// Cloudflare Access does not publish and which 404s on every team domain. The
/// consequences were invisible in every direction: <c>JsonWebTokenHandler</c>
/// swallows a metadata failure (IDX10261) into an EventSource no logger reads,
/// then validates with zero keys, so every request 401'd with IDX10500 while
/// the origin logged no retrieval attempt and the loopback healthcheck stayed
/// green. All 17 negative tests passed throughout — a server that authenticates
/// nobody refuses bad tokens perfectly.</para>
///
/// <para>So these tests assert the two things a stubbed key source cannot: the
/// URL production actually fetches, and that a real Cloudflare-shaped JWKS
/// document turns into an admitted request.</para>
/// </summary>
public class AccessSigningKeyTests
{
    private const string TeamDomain = "https://mailvec-test.cloudflareaccess.com";
    private const string OwnerAud = "owner-application-aud-tag";

    // ---------- the URL, which is what was wrong ----------

    [Fact]
    public void Keys_are_fetched_from_the_certs_endpoint_not_an_oidc_discovery_document()
    {
        var access = new AccessOptions { Enabled = true, TeamDomain = TeamDomain, Audience = OwnerAud };

        access.CertsAddress.ShouldBe($"{TeamDomain}/cdn-cgi/access/certs");
        // The regression, named. Cloudflare 404s this on every team domain.
        access.CertsAddress.ShouldNotContain(".well-known");
    }

    [Fact]
    public void A_trailing_slash_on_the_team_domain_does_not_double_up()
    {
        new AccessOptions { TeamDomain = TeamDomain + "/" }.CertsAddress
            .ShouldBe($"{TeamDomain}/cdn-cgi/access/certs");
    }

    [Fact]
    public void The_production_wiring_points_the_handler_at_that_exact_url()
    {
        // Reads the URL off the resolved JwtBearerOptions with nothing stubbed,
        // so it pins what the handler will really request. A test that only
        // asserted CertsAddress would still have passed while the value went
        // nowhere near the handler — which is close to what happened: the old
        // MetadataAddress was set correctly, to a URL that does not exist.
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<McpOptions>(o => o.Access = new AccessOptions
        {
            Enabled = true, TeamDomain = TeamDomain, Audience = OwnerAud,
        });
        AccessAuth.AddAccessAuthentication(services);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        options.ConfigurationManager.ShouldBeOfType<ConfigurationManager<OpenIdConnectConfiguration>>()
            .MetadataAddress.ShouldBe($"{TeamDomain}/cdn-cgi/access/certs");
    }

    // ---------- the document, through the real retriever ----------

    [Fact]
    public async Task A_cloudflare_shaped_jwks_yields_usable_signing_keys()
    {
        using var rsa = RSA.Create(2048);
        var retriever = new AccessCertsRetriever(TeamDomain);

        var configuration = await retriever.GetConfigurationAsync(
            $"{TeamDomain}/cdn-cgi/access/certs",
            new StubDocuments(Jwks(("kid-a", rsa), ("kid-b", rsa))),
            CancellationToken.None);

        configuration.Issuer.ShouldBe(TeamDomain);
        configuration.SigningKeys.Select(k => k.KeyId).ShouldBe(["kid-a", "kid-b"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_empty_key_set_throws_rather_than_configuring_zero_keys()
    {
        // A keyless configuration validates nothing and fails every request with
        // IDX10500 — the silent shape. Throwing keeps the last good key set on a
        // refresh and refuses the boot on a cold start.
        var retriever = new AccessCertsRetriever(TeamDomain);

        await Should.ThrowAsync<InvalidOperationException>(() => retriever.GetConfigurationAsync(
            $"{TeamDomain}/cdn-cgi/access/certs",
            new StubDocuments("""{"keys":[]}"""),
            CancellationToken.None));
    }

    [Fact]
    public async Task A_key_fetched_this_way_actually_admits_a_request()
    {
        // End-to-end through the REAL retriever and a REAL ConfigurationManager
        // — only the HTTP GET is stubbed. This is the path that was broken and
        // that no test covered: document in, authenticated caller out.
        using var factory = new StubbedDocumentFactory();

        var response = await factory.GetWithAssertion("/health", factory.Token());

        // 503, not 200: the fixture runs no Ollama, so /health reports degraded.
        // Reaching the handler at all is the assertion.
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task A_token_signed_by_a_key_absent_from_the_jwks_is_still_refused()
    {
        // Guards the obvious way to "fix" a retrieval bug badly: accepting keys
        // that aren't in the fetched set.
        using var factory = new StubbedDocumentFactory();
        using var stranger = RSA.Create(2048);

        var response = await factory.GetWithAssertion(
            "/health", factory.Token(new RsaSecurityKey(stranger) { KeyId = "kid-a" }));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---------- fail fast, rather than 401 everything ----------

    [Fact]
    public async Task The_server_refuses_to_start_when_the_keys_cannot_be_retrieved()
    {
        // The report's ask, and the more important half of the fix: an
        // unreachable key source must be a boot that never happened, not a
        // green container that denies all traffic.
        using var factory = new UnstubbedFactory(teamDomain: "https://127.0.0.1:1");

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await factory.GetWithAssertion("/health", "irrelevant"));

        // Names the URL and the knob — the two things the IDX208xx chain omits.
        error.Message.ShouldContain("127.0.0.1:1/cdn-cgi/access/certs");
        error.Message.ShouldContain("Mcp:Access:TeamDomain");
    }

    [Fact]
    public async Task Startup_verification_does_not_run_when_access_is_disabled()
    {
        // The loopback/launchd shape has no team domain and no Cloudflare. It
        // must not acquire a startup network call, let alone a new way to fail.
        using var factory = new MailvecMcpFactory();
        using var client = factory.CreateClient();

        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    // ---------- helpers ----------

    /// <summary>
    /// A JWKS in the shape Cloudflare Access serves from
    /// <c>/cdn-cgi/access/certs</c>: public RSA parameters, <c>use: sig</c>,
    /// <c>alg: RS256</c>. Hand-built rather than round-tripped through
    /// <c>JsonWebKeySet</c> so the document under test is the wire shape, not
    /// whatever the library happens to serialize.
    /// </summary>
    private static string Jwks(params (string Kid, RSA Key)[] keys) =>
        JsonSerializer.Serialize(new
        {
            keys = keys.Select(k =>
            {
                var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(
                    new RsaSecurityKey(k.Key.ExportParameters(false)) { KeyId = k.Kid });
                return new { kty = "RSA", use = "sig", alg = "RS256", kid = k.Kid, n = jwk.N, e = jwk.E };
            }).ToArray(),
        });

    private sealed class StubDocuments(string document) : IDocumentRetriever
    {
        public Task<string> GetDocumentAsync(string address, CancellationToken cancel) =>
            Task.FromResult(document);
    }

    /// <summary>The real server with Access on and NOTHING about key retrieval stubbed.</summary>
    private class UnstubbedFactory(string teamDomain = TeamDomain) : WebApplicationFactory<Program>
    {
        private readonly string _tempDir =
            Path.Combine(Path.GetTempPath(), "mailvec-keys-" + Guid.NewGuid().ToString("N"));

        protected string TeamDomainValue { get; } = teamDomain;

        public async Task<HttpResponseMessage> GetWithAssertion(string path, string assertion)
        {
            using var client = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add(AccessAuth.AssertionHeader, assertion);
            return await client.SendAsync(request);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempDir);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Archive:DatabasePath"] = Path.Combine(_tempDir, "archive.sqlite"),
                    ["Ollama:BaseUrl"] = "http://127.0.0.1:1",
                    ["Ollama:RequestTimeoutSeconds"] = "5",
                    ["Fastmail:AccountId"] = "",
                    ["Mcp:Access:Enabled"] = "true",
                    ["Mcp:Access:TeamDomain"] = TeamDomainValue,
                    ["Mcp:Access:Audience"] = OwnerAud,
                    ["Mcp:RestrictHealthToLoopback"] = "false",
                }));

            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(
                new RemoteIpStartupFilter(IPAddress.Parse("172.18.0.7"))));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// Same, but with the HTTP GET behind the key fetch replaced by a stub
    /// document. The retriever and the ConfigurationManager are the production
    /// ones, so this exercises everything between "Cloudflare served this JSON"
    /// and "the caller is authenticated".
    /// </summary>
    private sealed class StubbedDocumentFactory : UnstubbedFactory
    {
        private readonly RSA _rsa = RSA.Create(2048);

        public string Token(SecurityKey? signingKey = null) =>
            new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = TeamDomainValue,
                Audience = OwnerAud,
                NotBefore = DateTime.UtcNow.AddMinutes(-1),
                Expires = DateTime.UtcNow.AddHours(1),
                Claims = new Dictionary<string, object> { ["email"] = "owner@example.com" },
                SigningCredentials = new SigningCredentials(
                    signingKey ?? new RsaSecurityKey(_rsa) { KeyId = "kid-a" },
                    SecurityAlgorithms.RsaSha256),
            });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options => options.ConfigurationManager =
                    new ConfigurationManager<OpenIdConnectConfiguration>(
                        $"{TeamDomainValue}/cdn-cgi/access/certs",
                        new AccessCertsRetriever(TeamDomainValue),
                        new StubDocuments(Jwks(("kid-a", _rsa))))));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _rsa.Dispose();
        }
    }
}
