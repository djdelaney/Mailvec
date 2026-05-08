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

        // Build a combined chunk list per message: body chunks (source='body')
        // plus per-attachment chunks (source='attachment', attachment_id set).
        // chunk_index is unique per message and runs sequentially across both
        // sources — the schema's UNIQUE(message_id, chunk_index) requires it.
        var perMessageChunks = messageBatch
            .Select(m => (m.Id, Chunks: BuildChunksForMessage(m)))
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
        var attachmentChunkCount = 0;
        foreach (var (id, msgChunks) in perMessageChunks)
        {
            var vecs = allVectors.Skip(cursor).Take(msgChunks.Count).ToArray();
            chunks.ReplaceChunksForMessage(id, msgChunks, vecs, embeddedAt);
            cursor += msgChunks.Count;
            attachmentChunkCount += msgChunks.Count(c => c.Source == "attachment");
        }

        _processedThisRun += messageBatch.Count;
        logger.LogInformation(
            "Embedded {Messages} messages ({Chunks} chunks, {AttachmentChunks} from attachments) in {Ms}ms — {Done} done this run, {Remaining} remaining",
            perMessageChunks.Count, allTexts.Count, attachmentChunkCount, sw.ElapsedMilliseconds,
            _processedThisRun, messages.CountUnembedded());

        return messageBatch.Count;
    }

    /// <summary>
    /// Build the per-message chunk list combining body and attachment text.
    /// Body chunks come first (so chunk_index 0 is always body when present),
    /// followed by each attachment's chunks in part-index order. The chunker
    /// returns each list with its own 0-based Index — we renumber the
    /// combined list to keep <c>UNIQUE(message_id, chunk_index)</c> intact.
    ///
    /// Bodies under <see cref="EmbedderOptions.MinBodyCharsForVector"/> are
    /// dropped from the vector path: their embeddings would be dominated by
    /// the prepended subject and produce false-positive matches against any
    /// query sharing a subject token. The message remains searchable via the
    /// keyword/FTS leg. Attachment chunks still emit regardless — a thin
    /// email body wrapping a substantive PDF is exactly the case where
    /// attachment content indexing pays off.
    /// </summary>
    private IReadOnlyList<TextChunk> BuildChunksForMessage(UnembeddedMessage m)
    {
        var combined = new List<TextChunk>();

        var trimmedBodyLength = m.BodyText?.Trim().Length ?? 0;
        var minBodyChars = Math.Max(0, embedderOptions.Value.MinBodyCharsForVector);
        var bodyMeetsThreshold = trimmedBodyLength >= minBodyChars;

        if (bodyMeetsThreshold)
        {
            var bodyChunks = chunker.Chunk(BuildEmbeddingText(m.Subject, m.BodyText ?? string.Empty, m.AttachmentNames));
            foreach (var c in bodyChunks)
            {
                combined.Add(c with { Index = combined.Count, Source = "body", AttachmentId = null });
            }
        }

        foreach (var att in m.Attachments)
        {
            var prefixed = PrefixAttachmentText(att.FileName, att.Text);
            var attChunks = chunker.Chunk(prefixed);
            foreach (var c in attChunks)
            {
                combined.Add(c with
                {
                    Index = combined.Count,
                    Source = "attachment",
                    AttachmentId = att.AttachmentId,
                });
            }
        }

        return combined;
    }

    /// <summary>
    /// Prepend the attachment filename to its extracted text so a query that
    /// matches the document name (e.g. "2024 W-2") still ranks even if the
    /// filename token isn't repeated in the body. Mirrors the subject-prefix
    /// trick we use for the message body.
    /// </summary>
    private static string PrefixAttachmentText(string? fileName, string text)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return text;
        return $"{fileName}\n\n{text}";
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
    /// Prepend the subject and any attachment filenames to the body so they
    /// weight into the embedding. Marketing emails often have meaningful
    /// subjects with thin bodies, and replies the other way round; including
    /// both is robust. Attachment filenames help when the body is empty
    /// (e.g. statements arriving with one-line cover text).
    /// </summary>
    private static string BuildEmbeddingText(string? subject, string body, string? attachmentNames)
    {
        var hasSubject = !string.IsNullOrWhiteSpace(subject);
        var hasAttachments = !string.IsNullOrWhiteSpace(attachmentNames);
        if (!hasSubject && !hasAttachments) return body;

        var sb = new System.Text.StringBuilder();
        if (hasSubject) sb.Append(subject).Append("\n\n");
        if (hasAttachments) sb.Append("Attachments: ").Append(attachmentNames).Append("\n\n");
        sb.Append(body);
        return sb.ToString();
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
