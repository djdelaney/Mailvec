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

    // Prepended verbatim to SEARCH QUERIES (never documents) before
    // embedding. Instruction-tuned models like qwen3-embedding are trained
    // for asymmetric retrieval and bury relevant documents without it; for
    // those, set e.g.
    //   "Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery: "
    // Empty (the default) embeds the bare query — correct for symmetric
    // models like mxbai-embed-large. Applied in VectorSearchService, so the
    // same prefix reaches the CLI, MCP, tray, and eval paths identically.
    public string QueryInstructionPrefix { get; set; } = "";
}
