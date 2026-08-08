using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    string QuerySuffix,
    string DocumentPrefix,
    string DocumentSuffix,
    int MaxBatchSize,
    int RequestTimeoutSeconds,
    // Hosted-protocol capability policy (defaults describe the Ollama
    // protocol, which composes its own request): whether the wire `model`
    // field is sent, whether `dimensions` is requested, and the encoding
    // format to assert. Never auth material — the key stays out of this
    // displayable record by construction.
    bool SendWireModel = true,
    bool SendDimensions = false,
    string? EncodingFormat = null);

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

    /// <summary>
    /// Identity WITHOUT transport or credentials: registers only the
    /// resolved profile. For the indexer — it can be the first process to
    /// create the schema (SchemaMigrator stamps the profile's identity on a
    /// fresh database) but must NEVER receive the hosted API key, embed
    /// anything, or hold an embedding HttpClient. Resolution never touches
    /// key material, so this cannot leak a credential into the indexer's
    /// process however the profile is configured.
    /// </summary>
    public static IServiceCollection AddMailvecEmbeddingIdentity(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(Resolve(configuration));
        return services;
    }

    public static IServiceCollection AddMailvecEmbedding(
        this IServiceCollection services, IConfiguration configuration, EmbeddingClientRole role)
    {
        var resolved = Resolve(configuration);
        services.AddSingleton(resolved);

        // (The phase-2a PostConfigure bridge is retired: every consumer now
        // reads the resolved profile directly, so there is nothing left for
        // profile overrides to split-brain against.)

        if (resolved.Protocol == OpenAiCompatibleProtocol)
        {
            // The key is resolved ONCE here and captured by the HttpClient
            // configuration closure — it never touches the displayable
            // profile, descriptions, or health output. Missing key material
            // is fatal in EVERY process: unlike the OCR credential (scoped to
            // the embedder), query embedding needs it in MCP and the CLI too,
            // and a process that starts cleanly but can't embed queries is a
            // search outage wearing a green healthcheck.
            var bearerToken = ResolveBearerToken(configuration, resolved.Name);

            // Usage/rate-limit telemetry sink (Debug log lines the phase-6
            // audit greps). Optional by contract; TryAdd so a host can
            // substitute a richer observer.
            services.TryAddSingleton<IEmbeddingTelemetryObserver, LoggingEmbeddingTelemetryObserver>();

            var hostedHttp = services.AddHttpClient<OpenAiCompatibleTransport>((sp, client) =>
            {
                client.BaseAddress = new Uri(resolved.Endpoint);
                if (bearerToken is not null)
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
                // Same role split as Ollama: interactive queries get the tight
                // budget, ingestion the wide one (429/503 retries live in the
                // resilience handler, which honors Retry-After).
                client.Timeout = role == EmbeddingClientRole.BackgroundIngestion
                    ? TimeSpan.FromSeconds(330)
                    : TimeSpan.FromSeconds(Math.Max(5, resolved.RequestTimeoutSeconds));
            })
            // No legitimate inference call redirects, and a redirect must not
            // receive the bearer credential or a mail payload. Same rule as
            // the hosted OCR client.
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

            if (role == EmbeddingClientRole.BackgroundIngestion)
            {
                hostedHttp.AddStandardResilienceHandler(o =>
                {
                    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(120);
                    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(300);
                    o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(240);
                });
            }
            else
            {
                // Interactive hosted requests get a SMALL bounded retry, not
                // none: 429/503 are routine serverless conditions and the
                // standard handler honors Retry-After, while auth/model/4xx
                // errors are never retried. The budget respects a waiting
                // user — one retry inside ~10s total, nothing like the
                // ingestion pipeline's 300s. (Interactive OLLAMA keeps no
                // retry pipeline: against a local server a retry burns the
                // user's time without changing the outcome.)
                hostedHttp.AddStandardResilienceHandler(o =>
                {
                    o.Retry.MaxRetryAttempts = 1;
                    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(4);
                    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
                });
            }

            services.AddTransient<IEmbeddingTransport>(sp => sp.GetRequiredService<OpenAiCompatibleTransport>());
        }
        else
        {
            var http = services.AddHttpClient<OllamaClient>((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
                client.BaseAddress = new Uri(opts.BaseUrl);
                client.Timeout = role == EmbeddingClientRole.BackgroundIngestion
                    // The resilience handler below owns the per-attempt/total
                    // timeouts. HttpClient.Timeout wraps the entire handler
                    // chain — retries included — so it must sit ABOVE
                    // TotalRequestTimeout or it silently caps the pipeline
                    // (the old 60s default made the widened 120s/300s
                    // resilience timeouts dead config). But not infinite: the
                    // resilience timeouts cover up to response HEADERS, while
                    // the buffered body read happens under HttpClient.Timeout
                    // alone — an Ollama that returns 200 then stalls mid-body
                    // would hang the worker until SIGTERM. 330s = 300s total
                    // + body slack.
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

            services.AddTransient<IEmbeddingTransport>(sp => sp.GetRequiredService<OllamaClient>());
        }
        // The purpose-aware seam consumers actually use. Same resolved
        // profile object in every executable, so the query transform applied
        // at search time and the document transform applied at embed time
        // can never be two divergent config reads.
        services.AddTransient<IEmbeddingService>(sp =>
            new EmbeddingService(sp.GetRequiredService<IEmbeddingTransport>(), resolved));
        // Read-side identity enforcement for the semantic search path.
        services.AddTransient<EmbeddingSpaceGuard>();
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
            OpenAiCompatibleProtocol => ResolveOpenAiCompatibleProfile(embedding.ActiveProfile, profile),
            _ => throw new InvalidOperationException(
                $"Embedding profile '{embedding.ActiveProfile}' declares unknown protocol " +
                $"'{profile.Protocol}'. Known protocols: '{OllamaProtocol}', '{OpenAiCompatibleProtocol}'. " +
                "An unknown protocol never falls back to Ollama — fix the profile."),
        };
    }

    private static ResolvedEmbeddingProfile ResolveOpenAiCompatibleProfile(string name, EmbeddingProfileOptions profile)
    {
        // Endpoint: the complete embeddings URL, validated — no fragile
        // base-address path composition. HTTPS required except loopback (the
        // stub-test escape hatch); a bearer credential and mail content must
        // never travel plaintext.
        if (string.IsNullOrWhiteSpace(profile.Endpoint)
            || !Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException(
                $"Embedding profile '{name}': Endpoint must be the complete absolute embeddings URL.");
        if (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback)
            throw new InvalidOperationException(
                $"Embedding profile '{name}': Endpoint must be HTTPS (plain HTTP is allowed only for loopback test servers).");

        // Decision 3: hosted profiles MUST assert their space id. No provider
        // exposes anything trustworthy to derive it from — the wire model is
        // an alias (Fireworks serverless may move it; Baseten may take a
        // 'not-required' placeholder), and deriving would launder the alias
        // into looking like an identity.
        if (string.IsNullOrWhiteSpace(profile.SpaceId))
            throw new InvalidOperationException(
                $"Embedding profile '{name}': hosted profiles must assert an explicit SpaceId " +
                "(e.g. 'fireworks:qwen3-embedding-8b:1024:adopted-2026-08'). A wire model string is not " +
                "proof of vector compatibility — see docs/proposals/embedding-providers.md, decision 3.");

        var modelPolicy = profile.Request.ModelParameter.ToLowerInvariant();
        if (modelPolicy is not ("required" or "placeholder" or "omit"))
            throw new InvalidOperationException(
                $"Embedding profile '{name}': Request:ModelParameter must be 'required', 'placeholder', or 'omit'.");
        var sendModel = modelPolicy != "omit";
        var wireModel = profile.Request.Model;
        if (sendModel && string.IsNullOrWhiteSpace(wireModel))
            throw new InvalidOperationException(
                $"Embedding profile '{name}': Request:Model is required when ModelParameter is '{modelPolicy}'.");

        var dimsPolicy = profile.Request.DimensionsParameter.ToLowerInvariant();
        if (dimsPolicy is not ("send" or "omit"))
            throw new InvalidOperationException(
                $"Embedding profile '{name}': Request:DimensionsParameter must be 'send' or 'omit'.");

        // Always required and always validated even when it cannot be
        // requested — the returned width is checked against this either way.
        if (profile.OutputDimensions is not { } dims)
            throw new InvalidOperationException(
                $"Embedding profile '{name}': OutputDimensions is required for hosted profiles.");
        ArgumentOutOfRangeException.ThrowIfLessThan(dims, 1, nameof(profile.OutputDimensions));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dims, 8192, nameof(profile.OutputDimensions));

        if (profile.Request.EncodingFormat is { } enc && !string.Equals(enc, "float", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Embedding profile '{name}': EncodingFormat '{enc}' is unsupported — only 'float' " +
                "(a second vector-decoding path must be a deliberate change, not a config accident).");

        var scheme = profile.Auth.Scheme.ToLowerInvariant();
        if (scheme is not ("none" or "bearer"))
            throw new InvalidOperationException(
                $"Embedding profile '{name}': Auth:Scheme must be 'none' or 'bearer' — new auth behavior is code, not configuration.");

        return new ResolvedEmbeddingProfile(
            Name: name,
            Protocol: OpenAiCompatibleProtocol,
            ProviderId: profile.ProviderId ?? "custom",
            Endpoint: profile.Endpoint,
            WireModel: wireModel ?? "",
            OutputDimensions: dims,
            SpaceId: profile.SpaceId,
            // Hosted transforms default to EMPTY, never to the legacy
            // Ollama:QueryInstructionPrefix — that setting describes the
            // local model and inheriting it across providers would be a
            // silent space-affecting surprise.
            QueryPrefix: profile.Text.QueryPrefix ?? "",
            QuerySuffix: profile.Text.QuerySuffix ?? "",
            DocumentPrefix: profile.Text.DocumentPrefix ?? "",
            DocumentSuffix: profile.Text.DocumentSuffix ?? "",
            MaxBatchSize: Positive(profile.MaxBatchSize, 16, name, "MaxBatchSize"),
            RequestTimeoutSeconds: Positive(profile.RequestTimeoutSeconds, 60, name, "RequestTimeoutSeconds"),
            SendWireModel: sendModel,
            SendDimensions: dimsPolicy == "send",
            EncodingFormat: profile.Request.EncodingFormat?.ToLowerInvariant());
    }

    /// <summary>
    /// Resolve bearer key material at registration time — fatal when the
    /// scheme demands it and none exists. ApiKey (inline/env, for CI stubs
    /// and shell runs) wins over ApiKeyFile (owner-only file, the posture
    /// for long-running services). The value is returned to the HttpClient
    /// closure and stored nowhere else.
    /// </summary>
    internal static string? ResolveBearerToken(IConfiguration configuration, string profileName)
    {
        var embedding = new EmbeddingOptions();
        configuration.GetSection(EmbeddingOptions.SectionName).Bind(embedding);
        if (!embedding.Profiles.TryGetValue(profileName, out var profile)) return null;

        if (!string.Equals(profile.Auth.Scheme, "bearer", StringComparison.OrdinalIgnoreCase)) return null;

        if (!string.IsNullOrWhiteSpace(profile.Auth.ApiKey)) return profile.Auth.ApiKey.Trim();

        if (!string.IsNullOrWhiteSpace(profile.Auth.ApiKeyFile))
        {
            var path = PathExpansion.Expand(profile.Auth.ApiKeyFile);
            if (!File.Exists(path))
                throw new InvalidOperationException(
                    $"Embedding profile '{profileName}': Auth:ApiKeyFile '{path}' does not exist. " +
                    "Query embedding needs the key in every process (embedder, MCP, CLI) — a service that " +
                    "starts without it is a search outage wearing a green healthcheck.");
            var key = File.ReadAllText(path).Trim();
            if (key.Length == 0)
                throw new InvalidOperationException(
                    $"Embedding profile '{profileName}': Auth:ApiKeyFile '{path}' is empty.");
            return key;
        }

        throw new InvalidOperationException(
            $"Embedding profile '{profileName}': Auth:Scheme is 'bearer' but neither ApiKey nor ApiKeyFile " +
            "is configured. Secrets must not live in the shared appsettings.Local.json — use an owner-only " +
            "key file (ApiKeyFile) or an environment variable override for ApiKey.");
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
        QuerySuffix: "",
        DocumentPrefix: "",
        DocumentSuffix: "",
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
            // All four transforms are honored by EmbeddingService and covered
            // by the config hash — a profile carrying any of them is a
            // different vector space than one without.
            QueryPrefix: profile.Text.QueryPrefix ?? ollama.QueryInstructionPrefix,
            QuerySuffix: profile.Text.QuerySuffix ?? "",
            DocumentPrefix: profile.Text.DocumentPrefix ?? "",
            DocumentSuffix: profile.Text.DocumentSuffix ?? "",
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
