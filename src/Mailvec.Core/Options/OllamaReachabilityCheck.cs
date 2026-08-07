using Microsoft.Extensions.Logging;

namespace Mailvec.Core.Options;

/// <summary>
/// One-line startup diagnosis for the configuration that cannot possibly work:
/// <c>Ollama:BaseUrl</c> pointing at loopback from inside a container.
///
/// Why this earns a dedicated check. Ollama is reached over the network by both
/// the embedder (chunk vectors) and the MCP server (query vectors), and the
/// compose default is <c>http://localhost:11434</c>. Inside a container
/// "localhost" is that container, where nothing listens — so an operator who
/// comments <c>OLLAMA_BASE_URL</c> out of their .env does not disable Ollama,
/// they silently repoint it at nothing.
///
/// What that looked like before this check: a wall of
/// <c>SocketException (111): Connection refused</c> stack traces on every poll
/// forever, with the actual cause — an empty environment variable — appearing
/// nowhere in the output. The retry/resilience handler makes it worse by
/// producing several stack traces per attempt.
///
/// Deliberately a WARNING, not a fatal error. A loopback URL is legitimate on a
/// bare-metal install where Ollama runs on the same host, and this cannot tell
/// the two apart with certainty — only that the combination of "in a container"
/// and "loopback" has no working interpretation. Refusing to start would break
/// the local install to fix the containerised one.
/// </summary>
public static class OllamaReachabilityCheck
{
    /// <summary>
    /// Logs a single actionable warning when the configured Ollama URL is
    /// loopback and we appear to be containerised. No-op otherwise.
    /// </summary>
    public static void WarnIfUnreachableFromContainer(string? baseUrl, ILogger logger, bool? inContainer = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!(inContainer ?? RunningInContainer())) return;
        if (!IsLoopback(baseUrl)) return;

        logger.LogWarning(
            "Ollama:BaseUrl is {BaseUrl}, which inside a container means this container — nothing is listening there, " +
            "so every embedding call will fail with 'Connection refused'. Set OLLAMA_BASE_URL in the deployment's .env " +
            "to the address of the machine actually running Ollama (e.g. http://192.168.1.50:11434). " +
            "This is required regardless of Vision:Provider: a hosted OCR provider replaces the VISION model only, " +
            "never the embedding model.",
            baseUrl);
    }

    internal static bool IsLoopback(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// Best-effort containerisation check. <c>/.dockerenv</c> covers Docker;
    /// the DOTNET_RUNNING_IN_CONTAINER env var is set by Microsoft's own base
    /// images (so it also covers Podman and Kubernetes running those images).
    /// </summary>
    internal static bool RunningInContainer() =>
        Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
        || File.Exists("/.dockerenv");
}
