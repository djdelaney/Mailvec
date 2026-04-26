namespace Mailvec.Core.Options;

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string EmbeddingModel { get; set; } = "mxbai-embed-large";
    public int EmbeddingDimensions { get; set; } = 1024;
    public string KeepAlive { get; set; } = "30m";
    public int MaxBatchSize { get; set; } = 16;
    public int RequestTimeoutSeconds { get; set; } = 60;
}
