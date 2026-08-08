using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Embedding;

/// <summary>
/// How this process reaches the embedding provider. The two shapes exist for
/// a reason the proposal states outright: interactive query embedding gets a
/// tight budget (a user is waiting on search), background ingestion gets the
/// wide one (cold model loads take minutes and retries must be allowed to
/// run). What must NOT differ per process is which provider and which
/// vector space — that is the whole point of central registration.
/// </summary>
public enum EmbeddingClientRole
{
    /// <summary>MCP query embedding, CLI search/eval/doctor: short timeout, no retry pipeline.</summary>
    Interactive,

    /// <summary>The embedder's chunk ingestion: 330s ceiling + the standard resilience handler.</summary>
    BackgroundIngestion,
}

/// <summary>
/// The embedding-space identity and transport settings every executable must
/// agree on, resolved once from config. For the legacy path (no
/// <c>Embedding</c> section) every value comes from <see cref="OllamaOptions"/>;
/// an explicit Ollama profile may override the vector-affecting values, and
/// the resolver writes them BACK onto <see cref="OllamaOptions"/> via
/// PostConfigure so the many existing consumers that still read
/// <c>Ollama:*</c> directly cannot disagree with the profile. (That
/// back-write is the phase-2a bridge; the service/transport split retires
/// the direct reads.)
/// </summary>
public sealed record ResolvedEmbeddingProfile(
    string Name,
    string Protocol,
    string ProviderId,
    string Endpoint,
    string WireModel,
    int OutputDimensions,
    string SpaceId,
    string QueryPrefix,
    string DocumentPrefix,
    int MaxBatchSize,
    int RequestTimeoutSeconds);

/// <summary>
/// One place that decides which embedding provider a process gets — the
/// <see cref="VisionRegistration"/> pattern applied to the seam where drift
/// is catastrophic rather than inconvenient: the embedder writing one vector
/// space while MCP embeds queries into another produces plausible,
/// meaningless rankings with no error anywhere.
/// </summary>
public static class EmbeddingRegistration
{
    public const string OllamaProtocol = "ollama";
    public const string OpenAiCompatibleProtocol = "openai-compatible";

    public static IServiceCollection AddMailvecEmbedding(
        this IServiceCollection services, IConfiguration configuration, EmbeddingClientRole role)
    {
        var resolved = Resolve(configuration);
        services.AddSingleton(resolved);

        // Phase-2a bridge: consumers that still read Ollama:* directly
        // (EmbeddingWorker's verify, HealthService, VectorSearchService's
        // query prefix, EmbeddingSpace.FromOllamaOptions) see the profile's
        // resolved values, so a profile override cannot split-brain against
        // a direct read. Identity when no profile overrides anything.
        services.PostConfigure<OllamaOptions>(o =>
        {
            o.EmbeddingModel = resolved.WireModel;
            o.EmbeddingDimensions = resolved.OutputDimensions;
            o.QueryInstructionPrefix = resolved.QueryPrefix;
            o.MaxBatchSize = resolved.MaxBatchSize;
            o.RequestTimeoutSeconds = resolved.RequestTimeoutSeconds;
        });

        var http = services.AddHttpClient<OllamaClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = role == EmbeddingClientRole.BackgroundIngestion
                // The resilience handler below owns the per-attempt/total
                // timeouts. HttpClient.Timeout wraps the entire handler chain
                // — retries included — so it must sit ABOVE
                // TotalRequestTimeout or it silently caps the pipeline (the
                // old 60s default made the widened 120s/300s resilience
                // timeouts dead config). But not infinite: the resilience
                // timeouts cover up to response HEADERS, while the buffered
                // body read happens under HttpClient.Timeout alone — an
                // Ollama that returns 200 then stalls mid-body would hang
                // the worker until SIGTERM. 330s = 300s total + body slack.
                // (PingAsync stays bounded by its own 5s linked CTS.)
                ? TimeSpan.FromSeconds(330)
                : TimeSpan.FromSeconds(Math.Max(5, opts.RequestTimeoutSeconds));
        });

        if (role == EmbeddingClientRole.BackgroundIngestion)
        {
            http.AddStandardResilienceHandler(o =>
            {
                // Embedding a batch can be slow on first model load; widen
                // the per-attempt timeout.
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(120);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(300);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(240);
            });
        }

        services.AddTransient<IEmbeddingClient>(sp => sp.GetRequiredService<OllamaClient>());
        // The purpose-aware seam consumers actually use. Same resolved
        // profile object in every executable, so the query transform applied
        // at search time and the document transform applied at embed time
        // can never be two divergent config reads.
        services.AddTransient<IEmbeddingService>(sp =>
            new EmbeddingService(sp.GetRequiredService<IEmbeddingClient>(), resolved));
        return services;
    }

    /// <summary>
    /// Resolve the active profile against the legacy <c>Ollama:*</c> values.
    /// Public so tests (and later doctor) can inspect exactly what a given
    /// configuration means; the proposal requires the resolved profile to be
    /// fully displayable.
    /// </summary>
    public static ResolvedEmbeddingProfile Resolve(IConfiguration configuration)
    {
        var ollama = new OllamaOptions();
        configuration.GetSection(OllamaOptions.SectionName).Bind(ollama);

        var embedding = new EmbeddingOptions();
        configuration.GetSection(EmbeddingOptions.SectionName).Bind(embedding);

        if (string.IsNullOrWhiteSpace(embedding.ActiveProfile))
            return LegacyProfile(ollama);

        if (!embedding.Profiles.TryGetValue(embedding.ActiveProfile, out var profile))
            throw new InvalidOperationException(
                $"Embedding:ActiveProfile is '{embedding.ActiveProfile}' but no such profile is defined. " +
                (embedding.Profiles.Count == 0
                    ? "Embedding:Profiles is empty."
                    : $"Defined profiles: {string.Join(", ", embedding.Profiles.Keys)}."));

        return profile.Protocol switch
        {
            OllamaProtocol => ResolveOllamaProfile(embedding.ActiveProfile, profile, ollama),
            OpenAiCompatibleProtocol => throw new NotSupportedException(
                $"Embedding profile '{embedding.ActiveProfile}' uses protocol '{OpenAiCompatibleProtocol}', " +
                "which arrives with the hosted transport (phase 3 of docs/proposals/embedding-providers.md). " +
                "Until then only 'ollama' profiles can be activated."),
            _ => throw new InvalidOperationException(
                $"Embedding profile '{embedding.ActiveProfile}' declares unknown protocol " +
                $"'{profile.Protocol}'. Known protocols: '{OllamaProtocol}', '{OpenAiCompatibleProtocol}'. " +
                "An unknown protocol never falls back to Ollama — fix the profile."),
        };
    }

    private static ResolvedEmbeddingProfile LegacyProfile(OllamaOptions ollama) => new(
        Name: "ollama-legacy",
        Protocol: OllamaProtocol,
        ProviderId: "ollama",
        Endpoint: ollama.BaseUrl,
        WireModel: ollama.EmbeddingModel,
        OutputDimensions: ollama.EmbeddingDimensions,
        SpaceId: EmbeddingSpace.LegacySpaceId(ollama.EmbeddingModel, ollama.EmbeddingDimensions),
        QueryPrefix: ollama.QueryInstructionPrefix,
        DocumentPrefix: "",
        MaxBatchSize: ollama.MaxBatchSize,
        RequestTimeoutSeconds: ollama.RequestTimeoutSeconds);

    private static ResolvedEmbeddingProfile ResolveOllamaProfile(
        string name, EmbeddingProfileOptions profile, OllamaOptions ollama)
    {
        if (profile.Endpoint is not null)
            throw new InvalidOperationException(
                $"Embedding profile '{name}': Endpoint must not be set on an Ollama profile — the local " +
                "endpoint stays Ollama:BaseUrl (one setting for the server that embedding and vision share).");

        if (profile.SpaceId is not null)
            throw new InvalidOperationException(
                $"Embedding profile '{name}': SpaceId is asserted only for hosted protocols. Ollama space ids " +
                "are derived (ollama:<model>:<dims>) and enforced by the artifact digest — an asserted value " +
                "could only agree (redundant) or disagree (wrong).");

        // The purpose-aware service that honors suffixes and document
        // prefixes is the next phase-2 slice. Accepting them now would mean
        // silently not applying configured text transforms — the exact
        // quiet contract break the config hash exists to catch.
        if (!string.IsNullOrEmpty(profile.Text.QuerySuffix)
            || !string.IsNullOrEmpty(profile.Text.DocumentPrefix)
            || !string.IsNullOrEmpty(profile.Text.DocumentSuffix))
            throw new NotSupportedException(
                $"Embedding profile '{name}': QuerySuffix/DocumentPrefix/DocumentSuffix are not applied yet " +
                "(they arrive with the purpose-aware embedding service). Refusing to accept text transforms " +
                "that would be silently ignored.");

        var dims = profile.OutputDimensions ?? ollama.EmbeddingDimensions;
        ArgumentOutOfRangeException.ThrowIfLessThan(dims, 1, nameof(profile.OutputDimensions));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dims, 8192, nameof(profile.OutputDimensions));

        var model = profile.Request.Model ?? ollama.EmbeddingModel;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException($"Embedding profile '{name}': Request:Model resolved empty.");

        return new ResolvedEmbeddingProfile(
            Name: name,
            Protocol: OllamaProtocol,
            ProviderId: profile.ProviderId ?? "ollama",
            Endpoint: ollama.BaseUrl,
            WireModel: model,
            OutputDimensions: dims,
            SpaceId: EmbeddingSpace.LegacySpaceId(model, dims),
            QueryPrefix: profile.Text.QueryPrefix ?? ollama.QueryInstructionPrefix,
            DocumentPrefix: "",
            MaxBatchSize: Positive(profile.MaxBatchSize, ollama.MaxBatchSize, name, "MaxBatchSize"),
            RequestTimeoutSeconds: Positive(profile.RequestTimeoutSeconds, ollama.RequestTimeoutSeconds, name, "RequestTimeoutSeconds"));
    }

    private static int Positive(int? value, int fallback, string profile, string field)
    {
        var v = value ?? fallback;
        if (v < 1)
            throw new InvalidOperationException($"Embedding profile '{profile}': {field} must be >= 1 (was {v}).");
        return v;
    }
}
