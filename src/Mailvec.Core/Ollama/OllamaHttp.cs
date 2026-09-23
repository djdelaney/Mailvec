using Mailvec.Core.Options;

namespace Mailvec.Core.Ollama;

/// <summary>
/// The one way to build an Ollama <see cref="HttpClient"/> — for the
/// embedding client (<see cref="OllamaClient"/>, used by the embedder AND by
/// mcp/cli for query embeds) and the vision client
/// (<see cref="OllamaVisionClient"/>). Same three properties
/// <see cref="Parsing.ParserHttp"/> gives the parse client, for the same
/// reason: Ollama is reached over plain HTTP on the LAN with no
/// authentication, so anything that can answer for it — a compromised or
/// misconfigured host, an ARP-spoofing neighbour, a wrong
/// <c>Ollama:BaseUrl</c> — chooses the response these processes read.
///
/// <list type="bullet">
/// <item><b>No automatic redirects.</b> Every request carries mail text (an
/// embed batch) or a rendered page of mail (an OCR request). With redirects
/// on, a 307/308 makes the client resend that body to whatever
/// <c>Location</c> the responder names. A 3xx surfaces as a non-success
/// status and is classified like any other.</item>
/// <item><b>No proxy.</b> An <c>HTTP_PROXY</c> in a container's environment
/// would otherwise route every mail chunk through it.</item>
/// <item><b>A response ceiling</b> (<see cref="OllamaOptions.MaxResponseBytes"/>),
/// enforced by <see cref="HttpClient.MaxResponseContentBufferSize"/> while the
/// body is buffered — before any JSON is parsed — so a hostile responder
/// cannot make the archive-holding process allocate without bound. Every
/// Ollama call here buffers the whole response (the default
/// <c>ResponseContentRead</c>), which is what makes the ceiling apply.</item>
/// </list>
/// </summary>
public static class OllamaHttp
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
    };

    public static void ApplyResponseCeiling(HttpClient client, OllamaOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        client.MaxResponseContentBufferSize = Math.Max(1, options.MaxResponseBytes);
    }
}
