namespace Mailvec.Core.Options;

public sealed class EmbedderOptions
{
    public const string SectionName = "Embedder";

    public int PollIntervalSeconds { get; set; } = 30;
    public int ChunkSizeTokens { get; set; } = 400;
    public int ChunkOverlapTokens { get; set; } = 50;
    public int MaxConcurrentRequests { get; set; } = 2;
}
