using System.Globalization;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
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
    IEmbeddingClient ollama,
    IOptions<EmbedderOptions> embedderOptions,
    IOptions<OllamaOptions> ollamaOptions,
    ILogger<EmbeddingWorker> logger,
    AttachmentOcrService? ocr = null)
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
                // OCR scanned PDFs first (re-queues their messages), then embed.
                var ocred = await RunOcrIfEnabledAsync(stoppingToken).ConfigureAwait(false);

                var processed = await ProcessOneBatchAsync(batchSize, stoppingToken).ConfigureAwait(false);
                if (processed > 0)
                {
                    RecordBatchSuccess();
                }
                else if (ocred == 0)
                {
                    // Idle: nothing OCR'd and nothing embedded. Don't touch the
                    // success/failure counters — they reflect the outcome of
                    // the last *attempted* batch, not "the embedder is alive".
                    await Task.Delay(pollInterval, stoppingToken).ConfigureAwait(false);
                }
                // (ocred > 0 && processed == 0: OCR re-queued work — loop
                // immediately so the next embed batch picks it up.)
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordBatchFailure(ex);
                logger.LogError(ex, "Embedding batch failed; will retry after poll interval");
                await Task.Delay(pollInterval, stoppingToken).ConfigureAwait(false);
            }
        }

        logger.LogInformation("EmbeddingWorker stopping");
    }

    private async Task<int> RunOcrIfEnabledAsync(CancellationToken ct)
    {
        var opts = embedderOptions.Value;
        if (ocr is null || (!opts.OcrEnabled && !opts.ImageOcrEnabled)) return 0;

        // The renderer is native and platform-gated; the inline OS check both
        // satisfies CA1416 and short-circuits on an unsupported platform.
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() || OperatingSystem.IsWindows()))
            return 0;

        int ocred = 0;
        try
        {
            // Scanned PDFs first (the original pass), then image attachments —
            // both feed the same vision model and re-queue the message for embed.
            if (opts.OcrEnabled)
                ocred += await ocr.ProcessBatchAsync(opts.OcrBatchSize, ct).ConfigureAwait(false);
            if (opts.ImageOcrEnabled)
                ocred += await ocr.ProcessImageBatchAsync(opts.OcrBatchSize, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Per-item OCR failures are handled inside the pass; this guards the
            // batch enumeration / availability probe so an OCR problem never
            // stalls the embed pass.
            logger.LogError(ex, "OCR pass failed; continuing with embedding.");
        }
        return ocred;
    }

    internal async Task<int> ProcessOneBatchAsync(int batchSize, CancellationToken ct)
    {
        var messageBatch = messages.EnumerateUnembedded(batchSize).ToList();
        if (messageBatch.Count == 0) return 0;

        // Build a combined chunk list per message: body chunks (source='body')
        // plus per-attachment chunks (source='attachment', attachment_id set).
        // chunk_index is unique per message and runs sequentially across both
        // sources — the schema's UNIQUE(message_id, chunk_index) requires it.
        // Keep zero-chunk messages in the list (don't filter here) so the
        // stamping loop below can mark them embedded too. Otherwise messages
        // with body shorter than MinBodyCharsForVector and no extracted
        // attachment text would leak: they get no embedded_at stamp, get
        // re-fetched by EnumerateUnembedded next batch, and inflate
        // _processedThisRun by re-counting on every appearance.
        var perMessageChunks = messageBatch
            .Select(m => (m.Id, Chunks: BuildChunksForMessage(m)))
            .ToList();

        var allTexts = perMessageChunks.SelectMany(x => x.Chunks.Select(c => c.Text)).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Skip the embed call when nothing in the batch had chunks — all
        // messages still get stamped via the empty-chunks path below.
        var allVectors = allTexts.Count == 0
            ? Array.Empty<float[]>()
            : await EmbedInBatchesAsync(allTexts, batchSize, ct).ConfigureAwait(false);
        sw.Stop();

        if (allVectors.Length != allTexts.Count)
        {
            throw new InvalidOperationException(
                $"Embedded {allVectors.Length} vectors for {allTexts.Count} chunks");
        }

        var embeddedAt = DateTimeOffset.UtcNow;
        int cursor = 0;
        var attachmentChunkCount = 0;
        var nonEmptyMessageCount = 0;
        foreach (var (id, msgChunks) in perMessageChunks)
        {
            if (msgChunks.Count == 0)
            {
                // Stamp empty-chunk messages (short body AND no extractable
                // attachment) so EnumerateUnembedded stops returning them.
                chunks.ReplaceChunksForMessage(id, [], [], embeddedAt);
                continue;
            }
            var vecs = allVectors.Skip(cursor).Take(msgChunks.Count).ToArray();
            chunks.ReplaceChunksForMessage(id, msgChunks, vecs, embeddedAt);
            cursor += msgChunks.Count;
            attachmentChunkCount += msgChunks.Count(c => c.Source == "attachment");
            nonEmptyMessageCount++;
        }

        _processedThisRun += messageBatch.Count;
        logger.LogInformation(
            "Embedded {Messages} messages ({Chunks} chunks, {AttachmentChunks} from attachments) in {Ms}ms — {Done} done this run, {Remaining} remaining",
            nonEmptyMessageCount, allTexts.Count, attachmentChunkCount, sw.ElapsedMilliseconds,
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
    internal IReadOnlyList<TextChunk> BuildChunksForMessage(UnembeddedMessage m)
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
    /// Persist a "this batch succeeded" beat to the metadata table so the MCP
    /// server's /health endpoint (a separate process) can tell whether the
    /// embedder is making progress. The two services share state only through
    /// SQLite, so this is the cheapest single-row signal we can give them.
    /// Reset the consecutive-failures counter; only attempted batches touch
    /// these keys, so an idle embedder doesn't keep stamping fresh timestamps.
    /// </summary>
    private void RecordBatchSuccess()
    {
        var nowIso = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        metadata.Set(EmbedderHealthKeys.LastSuccessAt, nowIso);
        metadata.Set(EmbedderHealthKeys.ConsecutiveFailures, "0");
        metadata.Set(EmbedderHealthKeys.LastFailureKind, "");
    }

    /// <summary>
    /// Persist a "this batch failed" beat. Increments the consecutive-failures
    /// counter so HealthService can flip /health to degraded after N straight
    /// failures (the recurring orphan-vector / UNIQUE-constraint bug used to
    /// rack up thousands of these per day with no automated escalation).
    /// </summary>
    private void RecordBatchFailure(Exception ex)
    {
        var nowIso = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        // Best-effort increment. The embedder is the sole writer and a single
        // BackgroundService instance, so there's no in-process race; the only
        // other reader (HealthService) is read-only and tolerant of stale
        // values to the tune of one poll cycle.
        var prior = metadata.Get(EmbedderHealthKeys.ConsecutiveFailures);
        var next = int.TryParse(prior, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n + 1 : 1;
        metadata.Set(EmbedderHealthKeys.ConsecutiveFailures, next.ToString(CultureInfo.InvariantCulture));
        metadata.Set(EmbedderHealthKeys.LastFailureAt, nowIso);
        // Truncate so the metadata row stays cheap. Exception type name is
        // enough to disambiguate the common cases (SqliteException, HttpRequest-
        // Exception, etc.); message bodies belong in the rolling log file.
        metadata.Set(EmbedderHealthKeys.LastFailureKind, ex.GetType().Name);
    }

    /// <summary>
    /// Refuse to start if the configured model disagrees with the model whose
    /// vectors are already in chunk_embeddings. Mixing vector spaces silently
    /// produces meaningless similarity scores; the user must run `mailvec
    /// reindex` to switch models.
    /// </summary>
    internal void VerifyEmbeddingModelMatchesSchema()
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
