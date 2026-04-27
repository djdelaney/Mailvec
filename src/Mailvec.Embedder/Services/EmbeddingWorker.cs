using System.Globalization;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailvec.Embedder.Services;

public sealed class EmbeddingWorker(
    SchemaMigrator migrator,
    MetadataRepository metadata,
    MessageRepository messages,
    ChunkRepository chunks,
    ChunkingService chunker,
    OllamaClient ollama,
    IOptions<EmbedderOptions> embedderOptions,
    IOptions<OllamaOptions> ollamaOptions,
    ILogger<EmbeddingWorker> logger)
    : BackgroundService
{
    private int _processedThisRun;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        migrator.EnsureUpToDate();
        VerifyEmbeddingModelMatchesSchema();

        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, embedderOptions.Value.PollIntervalSeconds));
        var batchSize = Math.Max(1, ollamaOptions.Value.MaxBatchSize);

        logger.LogInformation(
            "EmbeddingWorker starting. Model={Model} Dim={Dim} BatchSize={Batch} Poll={Poll}s Remaining={Remaining}",
            ollamaOptions.Value.EmbeddingModel,
            ollamaOptions.Value.EmbeddingDimensions,
            batchSize,
            pollInterval.TotalSeconds,
            messages.CountUnembedded());

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessOneBatchAsync(batchSize, stoppingToken).ConfigureAwait(false);
                if (processed == 0)
                {
                    await Task.Delay(pollInterval, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Embedding batch failed; will retry after poll interval");
                await Task.Delay(pollInterval, stoppingToken).ConfigureAwait(false);
            }
        }

        logger.LogInformation("EmbeddingWorker stopping");
    }

    private async Task<int> ProcessOneBatchAsync(int batchSize, CancellationToken ct)
    {
        var messageBatch = messages.EnumerateUnembedded(batchSize).ToList();
        if (messageBatch.Count == 0) return 0;

        // Chunk every message in the batch up front so we can submit all chunks
        // in fewer Ollama calls (model load overhead matters more than batch size here).
        var perMessageChunks = messageBatch
            .Select(m => (m.Id, Chunks: chunker.Chunk(BuildEmbeddingText(m.Subject, m.BodyText))))
            .Where(x => x.Chunks.Count > 0)
            .ToList();

        if (perMessageChunks.Count == 0)
        {
            // Mark messages with no embeddable text as embedded=now so we don't retry forever.
            var now = DateTimeOffset.UtcNow;
            foreach (var m in messageBatch)
            {
                chunks.ReplaceChunksForMessage(m.Id, [], [], now);
            }
            _processedThisRun += messageBatch.Count;
            logger.LogDebug("Marked {Count} empty-body messages as embedded", messageBatch.Count);
            return messageBatch.Count;
        }

        var allTexts = perMessageChunks.SelectMany(x => x.Chunks.Select(c => c.Text)).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var allVectors = await EmbedInBatchesAsync(allTexts, batchSize, ct).ConfigureAwait(false);
        sw.Stop();

        if (allVectors.Length != allTexts.Count)
        {
            throw new InvalidOperationException(
                $"Embedded {allVectors.Length} vectors for {allTexts.Count} chunks");
        }

        var embeddedAt = DateTimeOffset.UtcNow;
        int cursor = 0;
        foreach (var (id, msgChunks) in perMessageChunks)
        {
            var vecs = allVectors.Skip(cursor).Take(msgChunks.Count).ToArray();
            chunks.ReplaceChunksForMessage(id, msgChunks, vecs, embeddedAt);
            cursor += msgChunks.Count;
        }

        _processedThisRun += messageBatch.Count;
        logger.LogInformation(
            "Embedded {Messages} messages ({Chunks} chunks) in {Ms}ms — {Done} done this run, {Remaining} remaining",
            perMessageChunks.Count, allTexts.Count, sw.ElapsedMilliseconds,
            _processedThisRun, messages.CountUnembedded());

        return messageBatch.Count;
    }

    private async Task<float[][]> EmbedInBatchesAsync(IReadOnlyList<string> inputs, int batchSize, CancellationToken ct)
    {
        var results = new List<float[]>(inputs.Count);
        for (int i = 0; i < inputs.Count; i += batchSize)
        {
            var slice = inputs.Skip(i).Take(batchSize).ToList();
            var vecs = await ollama.EmbedAsync(slice, ct).ConfigureAwait(false);
            results.AddRange(vecs);
        }
        return [.. results];
    }

    /// <summary>
    /// Prepend the subject to the body so it weights into the embedding. Marketing
    /// emails often have meaningful subjects with thin bodies, and replies the
    /// other way round; including both is robust.
    /// </summary>
    private static string BuildEmbeddingText(string? subject, string body)
    {
        if (string.IsNullOrWhiteSpace(subject)) return body;
        return $"{subject}\n\n{body}";
    }

    /// <summary>
    /// Refuse to start if the configured model disagrees with the model whose
    /// vectors are already in chunk_embeddings. Mixing vector spaces silently
    /// produces meaningless similarity scores; the user must run `mailvec
    /// reindex` to switch models.
    /// </summary>
    private void VerifyEmbeddingModelMatchesSchema()
    {
        var configuredModel = ollamaOptions.Value.EmbeddingModel;
        var configuredDim = ollamaOptions.Value.EmbeddingDimensions;

        var existingModel = metadata.Get("embedding_model");
        var existingDim = metadata.Get("embedding_dimensions");

        if (existingModel is null || existingDim is null)
        {
            metadata.Set("embedding_model", configuredModel);
            metadata.Set("embedding_dimensions", configuredDim.ToString(CultureInfo.InvariantCulture));
            logger.LogInformation("Initialised metadata: model={Model} dim={Dim}", configuredModel, configuredDim);
            return;
        }

        if (!string.Equals(existingModel, configuredModel, StringComparison.Ordinal) ||
            existingDim != configuredDim.ToString(CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException(
                $"Embedding model mismatch. Database was built with {existingModel} ({existingDim} dims); " +
                $"config requests {configuredModel} ({configuredDim} dims). " +
                $"Run `mailvec reindex --all` to rebuild vectors with the new model.");
        }
    }
}
