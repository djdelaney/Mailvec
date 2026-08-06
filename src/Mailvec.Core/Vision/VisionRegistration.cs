using Mailvec.Core.Mistral;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Vision;

/// <summary>
/// One place that decides which <see cref="IVisionClient"/> a process gets.
///
/// Three executables register the vision client (Embedder for the OCR passes,
/// Cli for <c>mailvec doctor</c>, Mcp for <c>/health</c>), and before this they
/// each hard-wired <c>OllamaVisionClient</c>. Provider selection duplicated
/// three ways is exactly the kind of drift that ends with the embedder OCRing
/// against one provider while doctor cheerfully reports on another.
/// </summary>
public static class VisionRegistration
{
    /// <summary>
    /// Bind <c>Vision:*</c> and register the configured provider's client,
    /// along with its own HttpClient (the vision timeout is much longer than
    /// the embed timeout — OCR runs for tens of seconds).
    ///
    /// Ollama stays the default, so an existing local install needs no config
    /// change and a future GPU can be switched back to with one setting.
    /// </summary>
    /// <param name="requiresCredentials">
    /// True for the process that actually performs OCR (the embedder): incomplete
    /// provider config is fatal, because the alternative is a service that starts
    /// cleanly and quietly never OCRs anything.
    ///
    /// False for processes that only PROBE the provider (the MCP server's
    /// /health, the CLI's doctor). They must not refuse to start over an OCR
    /// misconfiguration — crashlooping the MCP container would take down search
    /// and every tool call because a vision API key was missing, which is wildly
    /// disproportionate to the fault. They degrade to reporting OCR unavailable,
    /// which is both true and exactly the signal the operator needs.
    /// </param>
    public static IServiceCollection AddMailvecVision(
        this IServiceCollection services, IConfiguration configuration, bool requiresCredentials = false)
    {
        services.Configure<VisionOptions>(configuration.GetSection(VisionOptions.SectionName));

        var vision = new VisionOptions();
        configuration.GetSection(VisionOptions.SectionName).Bind(vision);

        // A bad provider NAME is always fatal, in every process: it means the
        // operator asked for something that doesn't exist, and silently falling
        // back to a local model that isn't running looks identical to "OCR is
        // quietly doing nothing" — the failure we can least afford to hide.
        vision.ValidateProviderName();

        if (vision.IsMistral)
        {
            if (requiresCredentials)
            {
                vision.Mistral.Validate();
            }
            else if (!vision.Mistral.IsComplete)
            {
                // Probe-only process with no credentials to probe with. Report
                // unavailable rather than throwing — see requiresCredentials.
                services.AddSingleton<IVisionClient>(new UnconfiguredVisionClient());
                return services;
            }

            services.AddHttpClient<MistralOcrClient>((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<VisionOptions>>().Value.Mistral;
                client.BaseAddress = new Uri(opts.Endpoint.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(Math.Max(30, opts.RequestTimeoutSeconds));
                MistralOcrClient.ApplyAuth(client, opts);
            });
            services.AddTransient<IVisionClient>(sp => sp.GetRequiredService<MistralOcrClient>());
            return services;
        }

        services.AddHttpClient<OllamaVisionClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(Math.Max(30, opts.VisionRequestTimeoutSeconds));
        });
        services.AddTransient<IVisionClient>(sp => sp.GetRequiredService<OllamaVisionClient>());
        return services;
    }

    /// <summary>
    /// Human-readable identity of the configured provider, for logs,
    /// <c>mailvec doctor</c> and <c>/health</c>. Never includes the API key.
    /// </summary>
    public static string Describe(VisionOptions vision, OllamaOptions ollama) =>
        vision.IsMistral
            ? $"mistral-ocr {(string.IsNullOrWhiteSpace(vision.Mistral.Model) ? "(unconfigured)" : vision.Mistral.Model)}" +
              $" @ {(string.IsNullOrWhiteSpace(vision.Mistral.Endpoint) ? "(unconfigured)" : vision.Mistral.Endpoint)}"
            : $"ollama {ollama.VisionModel}";
}

/// <summary>
/// Stand-in for a probe-only process configured for a hosted provider it has no
/// credentials for — the MCP server and the CLI when the API key is
/// deliberately kept out of their environment.
///
/// Reports unavailable rather than throwing at startup. The alternative was an
/// MCP container that crashloops (taking search and every tool call with it)
/// because an OCR key was missing, which is wildly out of proportion to the
/// fault. Every OCR call throws, loudly and classified, because a process that
/// reached one had no business doing so.
/// </summary>
internal sealed class UnconfiguredVisionClient : IVisionClient
{
    private const string Message =
        "Vision:Provider is 'mistral' but this process has no credentials configured for it. " +
        "That is expected for the MCP server and CLI when the API key is scoped to the embedder; " +
        "OCR itself runs in the embedder.";

    public Task<string> OcrAsync(byte[] image, CancellationToken ct = default) =>
        throw new VisionException(VisionFailureKind.AuthOrConfig, Message);

    public Task<string> OcrImageAsync(byte[] image, CancellationToken ct = default) =>
        throw new VisionException(VisionFailureKind.AuthOrConfig, Message);

    public Task<bool> IsModelAvailableAsync(CancellationToken ct = default) => Task.FromResult(false);
}
