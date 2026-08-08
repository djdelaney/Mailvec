using Mailvec.Core.Embedding;

namespace Mailvec.Core.Tests.Embedding;

/// <summary>
/// Shared resolved-profile fixtures for tests that wrap an
/// <see cref="IEmbeddingTransport"/> fake in the real <see cref="EmbeddingService"/> —
/// which is the point: the purpose-aware layer (text policy, probe
/// classification) is production code under test, not something each fake
/// re-implements.
/// </summary>
public static class TestProfiles
{
    public static ResolvedEmbeddingProfile Legacy(string queryPrefix = "") => new(
        Name: "ollama-legacy",
        Protocol: "ollama",
        ProviderId: "ollama",
        Endpoint: "http://localhost:11434",
        WireModel: "mxbai-embed-large",
        OutputDimensions: 1024,
        SpaceId: "ollama:mxbai-embed-large:1024",
        QueryPrefix: queryPrefix,
        QuerySuffix: "",
        DocumentPrefix: "",
        DocumentSuffix: "",
        MaxBatchSize: 16,
        RequestTimeoutSeconds: 60);
}
