namespace Mailvec.Core.Options;

/// <summary>
/// Named embedding profiles (phase 2 of docs/proposals/embedding-providers.md).
/// When this section is absent, the legacy resolver derives an Ollama profile
/// from <see cref="OllamaOptions"/> so existing installations select Ollama
/// with no config change. Selection happens once, in
/// <c>EmbeddingRegistration.AddMailvecEmbedding</c> — never per-executable.
/// </summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    /// <summary>Profile name to activate. Null/empty selects the legacy Ollama derivation.</summary>
    public string? ActiveProfile { get; set; }

    public Dictionary<string, EmbeddingProfileOptions> Profiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One profile. String properties are nullable on purpose: null means
/// "inherit the legacy Ollama value", while an explicit empty string is a
/// real value (an empty query prefix is a meaningful choice). The binder
/// cannot express that distinction with non-nullable defaults.
/// </summary>
public sealed class EmbeddingProfileOptions
{
    /// <summary>"ollama" today; "openai-compatible" arrives with the phase-3 transport. Anything else is fatal at startup — never a silent fallback.</summary>
    public string Protocol { get; set; } = "";

    /// <summary>Stable diagnostic label. Never affects serialization and never stands in for SpaceId.</summary>
    public string? ProviderId { get; set; }

    /// <summary>Full endpoint URL for hosted protocols. Must be null for Ollama profiles — the local endpoint stays Ollama:BaseUrl, shared with vision.</summary>
    public string? Endpoint { get; set; }

    public EmbeddingRequestOptions Request { get; set; } = new();

    /// <summary>Always validated against returned vectors once resolved; null inherits Ollama:EmbeddingDimensions.</summary>
    public int? OutputDimensions { get; set; }

    /// <summary>
    /// Operator-asserted embedding-space identity — REQUIRED for hosted
    /// protocols (decision 3: no provider exposes anything trustworthy to
    /// derive it from) and FORBIDDEN for Ollama profiles, where it is derived
    /// (`ollama:&lt;model&gt;:&lt;dims&gt;`) and the artifact digest supplies the
    /// enforcement.
    /// </summary>
    public string? SpaceId { get; set; }

    public EmbeddingTextOptions Text { get; set; } = new();

    public int? MaxBatchSize { get; set; }
    public int? RequestTimeoutSeconds { get; set; }
}

public sealed class EmbeddingRequestOptions
{
    /// <summary>Wire model value; null inherits Ollama:EmbeddingModel for Ollama profiles.</summary>
    public string? Model { get; set; }

    /// <summary>"required" | "placeholder" | "omit" — hosted-protocol policy; parsed now, honored by the phase-3 transport.</summary>
    public string ModelParameter { get; set; } = "required";

    /// <summary>"send" | "omit" — hosted-protocol policy; parsed now, honored by the phase-3 transport.</summary>
    public string DimensionsParameter { get; set; } = "send";

    public string? EncodingFormat { get; set; }
}

public sealed class EmbeddingTextOptions
{
    public string? QueryPrefix { get; set; }
    public string? QuerySuffix { get; set; }
    public string? DocumentPrefix { get; set; }
    public string? DocumentSuffix { get; set; }
}
