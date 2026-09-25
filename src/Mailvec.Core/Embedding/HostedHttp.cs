namespace Mailvec.Core.Embedding;

/// <summary>
/// The handler and response ceiling for clients that call a HOSTED provider
/// with a credential attached: the OpenAI-compatible embedding transport
/// (Fireworks et al.) and mistral-ocr. The same three rules as
/// <see cref="Parsing.ParserHttp"/> and <see cref="Ollama.OllamaHttp"/>:
/// <list type="bullet">
/// <item><b>No redirects</b> — a redirect would resend the bearer token (or a
/// custom auth header, which HttpClient does not strip cross-host) and the
/// mail payload to a <c>Location</c> the responder chose.</item>
/// <item><b>No proxy</b> — an <c>HTTP_PROXY</c>/<c>HTTPS_PROXY</c> in a
/// container's environment would otherwise route the credential and every
/// request through it. These endpoints are reached directly. The one
/// exception is an embedding profile that opts in with
/// <c>Proxy=environment</c>, which registration permits only for a profile
/// holding no key (Auth:Scheme=none) — see
/// <c>EmbeddingProfileOptions.Proxy</c>.</item>
/// <item><b>A response ceiling</b> — the provider's answer is buffered and
/// parsed inside the archive-holding process; a compromised or misbehaving
/// provider (or a TLS-intercepting middlebox) should not decide how much
/// memory that takes. 64 MB is far above an embedding batch (~5 MB at 64 x
/// 4096 dims) or an OCR page, which <c>MaxCharsPerCall</c> caps at 24k
/// chars.</item>
/// </list>
/// </summary>
public static class HostedHttp
{
    public const long MaxResponseBytes = 64L * 1024 * 1024;

    public static SocketsHttpHandler CreateHandler() => CreateHandler(useEnvironmentProxy: false);

    /// <param name="useEnvironmentProxy">
    /// Route through the proxy the environment names (HTTP(S)_PROXY, NO_PROXY).
    /// Only a keyless embedding profile may ask for it; every other rule stands.
    /// </param>
    public static SocketsHttpHandler CreateHandler(bool useEnvironmentProxy) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = useEnvironmentProxy,
        Proxy = useEnvironmentProxy ? HttpClient.DefaultProxy : null,
        UseCookies = false,
    };

    public static void ApplyResponseCeiling(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.MaxResponseContentBufferSize = MaxResponseBytes;
    }
}
