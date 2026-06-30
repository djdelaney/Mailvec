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

    // Vision model for scanned-PDF OCR (the embedder's OCR pass). See
    // docs/contributing/attachment-ocr.md.
    public string VisionModel { get; set; } = "qwen2.5vl:7b";

    // Empty -> omit keep_alive from the OCR request so Ollama uses its own
    // default (~5 min) and unloads the 6GB vision model when idle. Scans arrive
    // rarely, so pinning it isn't worth the RAM; never set "-1" (pin forever).
    public string VisionKeepAlive { get; set; } = "";

    // OCR a page can take tens of seconds — well over the embed timeout — so the
    // vision HttpClient gets its own, longer ceiling.
    public int VisionRequestTimeoutSeconds { get; set; } = 120;

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
