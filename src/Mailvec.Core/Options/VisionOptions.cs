namespace Mailvec.Core.Options;

/// <summary>
/// Which vision provider backs the embedder's OCR passes, and how to reach it
/// when that provider is hosted.
///
/// Split out of <see cref="OllamaOptions"/> rather than bolted onto it: an
/// <c>Ollama:VisionModel</c> is meaningless under a Mistral provider, and one
/// section holding two mutually exclusive sets of knobs invites a deployment
/// that silently reads the wrong half. The Ollama knobs stay exactly where they
/// were, so an existing local install keeps working with no config change — the
/// default provider is still Ollama.
/// </summary>
public sealed class VisionOptions
{
    public const string SectionName = "Vision";

    /// <summary>
    /// <c>ollama</c> (default — the local vision model) or <c>mistral</c> (the
    /// hosted mistral-ocr API, incl. an Azure AI Foundry deployment).
    /// Case-insensitive; an unrecognised value fails fast at startup rather
    /// than silently falling back, because falling back to a local model that
    /// isn't there looks exactly like "OCR is quietly off".
    /// </summary>
    public string Provider { get; set; } = ProviderOllama;

    public const string ProviderOllama = "ollama";
    public const string ProviderMistral = "mistral";

    public MistralVisionOptions Mistral { get; set; } = new();

    public bool IsMistral => string.Equals(Provider, ProviderMistral, StringComparison.OrdinalIgnoreCase);
    public bool IsOllama => string.Equals(Provider, ProviderOllama, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Throws when the configured provider isn't one we implement. Checked in
    /// every process — an unknown name means the operator asked for something
    /// that doesn't exist, and falling back silently would look exactly like
    /// "OCR is quietly doing nothing".
    /// </summary>
    public void ValidateProviderName()
    {
        if (!IsOllama && !IsMistral)
            throw new InvalidOperationException(
                $"Vision:Provider must be '{ProviderOllama}' or '{ProviderMistral}', got '{Provider}'.");
    }

    /// <summary>Provider name AND, for a hosted provider, its credentials.</summary>
    public void Validate()
    {
        ValidateProviderName();
        if (IsMistral) Mistral.Validate();
    }
}

/// <summary>
/// Connection settings for the hosted mistral-ocr API. Works against both
/// api.mistral.ai and an Azure AI Foundry deployment — only
/// <see cref="Route"/>, <see cref="Model"/> and <see cref="AuthHeader"/> differ.
/// </summary>
public sealed class MistralVisionOptions
{
    /// <summary>Base URL, e.g. <c>https://&lt;resource&gt;.services.ai.azure.com</c>.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>
    /// Path under <see cref="Endpoint"/>. Azure AI Foundry serves
    /// <c>providers/mistral/azure/ocr</c>; api.mistral.ai serves <c>v1/ocr</c>.
    /// Observed 2026-08-06 — verify rather than trust, the other candidate
    /// routes 404.
    /// </summary>
    public string Route { get; set; } = "providers/mistral/azure/ocr";

    /// <summary>
    /// On Azure this is the **deployment** name (e.g. <c>mistral-ocr-4-0</c>),
    /// not a public model id.
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// API key. **Never put this in the shared appsettings.Local.json** — that
    /// file is world-readable (0644) and holds ordinary user settings. Supply it
    /// through the environment (<c>Vision__Mistral__ApiKey</c>), which is what
    /// compose does from its .env.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary><c>bearer</c> (default) or a literal header name such as <c>api-key</c>.</summary>
    public string AuthHeader { get; set; } = "bearer";

    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Minimum gap between calls. The OCR pass is naturally paced (a small batch
    /// every poll), but a backfill drains as fast as the service allows, and a
    /// throttled provider is worse than a slow one — see
    /// <see cref="Vision.VisionFailureKind.Backpressure"/>.
    /// </summary>
    public int MinIntervalMs { get; set; } = 250;

    /// <summary>
    /// In-client retries on 429/5xx before the failure is surfaced as
    /// Backpressure. Deliberately small: the OCR pass re-runs every poll, so
    /// giving up quickly and retrying next cycle is cheaper than holding a
    /// worker on a long backoff.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Hard ceiling on characters kept from one call. Stands in for Ollama's
    /// <c>num_predict</c>, which has no hosted equivalent — without a bound, a
    /// repetition-looping response is stored and indexed in full. Measured: a
    /// dense architectural drawing produced 3562 chars, most of it one table row
    /// repeating, and it defeated CollapseRepeatedLines because the repetition
    /// was *within* a single line. 24k ≈ a very dense page with headroom.
    /// </summary>
    public int MaxCharsPerCall { get; set; } = 24_000;

    /// <summary>
    /// Whether enough is set to attempt a call. Lets a probe-only process
    /// (MCP /health, CLI doctor) tell "no credentials here, by design" from
    /// "credentials are wrong" without throwing at startup.
    /// </summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Endpoint)
        && !string.IsNullOrWhiteSpace(Model)
        && !string.IsNullOrWhiteSpace(ApiKey);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
            throw new InvalidOperationException("Vision:Mistral:Endpoint is required when Vision:Provider=mistral.");
        if (string.IsNullOrWhiteSpace(Model))
            throw new InvalidOperationException("Vision:Mistral:Model (the deployment name) is required when Vision:Provider=mistral.");
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException(
                "Vision:Mistral:ApiKey is required when Vision:Provider=mistral. " +
                "Supply it via the Vision__Mistral__ApiKey environment variable, not the shared appsettings.Local.json (world-readable).");
    }
}
