using Mailvec.Core.Options;

namespace Mailvec.Core.Parsing;

/// <summary>
/// The one way to build the <see cref="RemoteParser"/>'s <see cref="HttpClient"/>.
/// <see cref="ParserRegistration"/> uses it for the named client and the test
/// fixture uses it for ad-hoc endpoints, so the two cannot drift: a client
/// built any other way would silently lack the two properties below that
/// keep a compromised parse service from turning its callers against them.
///
/// <list type="bullet">
/// <item><b>No automatic redirects.</b> The callers POST whole <c>.eml</c>
/// bodies. With redirects on, a 307/308 from the service makes the caller
/// resend that body to whatever <c>Location</c> the service names — and the
/// embedder and mcp have the LAN egress the parse container was denied. A
/// 3xx is classified as <see cref="ParseFailureKind.Crashed"/> instead.</item>
/// <item><b>No proxy.</b> The endpoint is a compose-internal name; an
/// <c>HTTP_PROXY</c> in the environment would otherwise route every message
/// through it.</item>
/// <item><b>A response ceiling</b> (<see cref="ParserOptions.MaxResponseBytes"/>),
/// enforced by <see cref="HttpClient.MaxResponseContentBufferSize"/> while the
/// body is read, so the service cannot allocate gigabytes in the process
/// that holds the archive.</item>
/// </list>
/// </summary>
public static class ParserHttp
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
    };

    public static void Configure(HttpClient client, ParserOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            var endpoint = options.Endpoint.Trim();
            client.BaseAddress = new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + "/");
        }
        client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds));
        client.MaxResponseContentBufferSize = Math.Max(1, options.MaxResponseBytes);
    }

    /// <summary>A standalone client with every property above — for callers outside the DI container.</summary>
    public static HttpClient CreateClient(ParserOptions options)
    {
        var client = new HttpClient(CreateHandler(), disposeHandler: true);
        Configure(client, options);
        return client;
    }
}
