using System.Runtime.Versioning;
using System.Text;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Vision;
using Mailvec.Pdf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailvec.Embedder.Services;

/// <summary>
/// The embedder's scanned-PDF OCR pass: finds attachments stuck at
/// extraction_status='no_text', renders each page (PDFium) and transcribes it
/// with the Ollama vision model, then writes the text back (status='ocr') and
/// re-queues the parent message for embedding — so a previously-unsearchable
/// scan becomes searchable. Runs before the embed pass each cycle; see
/// docs/contributing/attachment-ocr.md.
///
/// Platform-gated because the renderer is native; the embedder only runs on
/// macOS / Linux / Windows.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public sealed class AttachmentOcrService(
    MessageRepository messages,
    MaildirAttachmentReader reader,
    IVisionClient vision,
    IOptions<EmbedderOptions> options,
    ILogger<AttachmentOcrService> logger)
{
    private readonly int _maxPages = Math.Max(1, options.Value.OcrMaxPagesPerPdf);
    private readonly EmbedderOptions _opts = options.Value;

    // Per-attachment vision-failure counts, in-memory for this process. A vision
    // call that fails every cycle for one document would otherwise head-of-line
    // block the whole OCR queue forever (candidates are ordered by id, so the
    // same low-id poison doc is re-selected first each cycle). After this many
    // counted failures we retire it to 'failed' and move on. Only the single
    // embedder worker touches this, sequentially, so a plain dict is safe.
    //
    // IMPORTANT: failures only COUNT toward retirement in cycles where some
    // other vision call succeeded (see SettleVisionFailures). The model-
    // availability probe is a /api/tags name check, which answers 200 even
    // when Ollama can't actually load the model (GPU OOM, dead runner) — in
    // that wedged state every call times out, and counting those failures
    // would permanently retire perfectly good scans, one head-of-queue
    // document at a time, for as long as the outage lasted. A same-cycle
    // success is the evidence that distinguishes "this document is poison"
    // from "the model can't run at all".
    private const int MaxVisionAttempts = 5;

    // How many consecutive vision failures within one cycle before we stop
    // trying further candidates (the model is likely wedged; back off until
    // the next poll rather than burning a timeout per candidate).
    private const int MaxConsecutiveCycleFailures = 2;

    private readonly Dictionary<long, int> _visionFailures = new();

    // Returns true when the attachment has failed enough counted times to retire.
    private bool RecordVisionFailure(long attachmentId)
    {
        var n = _visionFailures.GetValueOrDefault(attachmentId) + 1;
        if (n >= MaxVisionAttempts) { _visionFailures.Remove(attachmentId); return true; }
        _visionFailures[attachmentId] = n;
        return false;
    }

    /// <summary>
    /// End-of-cycle bookkeeping for vision failures. With at least one
    /// successful vision call this cycle, the model is demonstrably able to
    /// run — failures are document-specific, so they count toward retirement
    /// (and hit 'failed' after <see cref="MaxVisionAttempts"/> counted
    /// cycles). With zero successes we can't tell a poison document from a
    /// wedged Ollama, so nothing is counted and everything retries next cycle.
    /// </summary>
    private void SettleVisionFailures(IReadOnlyList<long> failedAttachmentIds, int visionSuccesses, string pass)
    {
        if (failedAttachmentIds.Count == 0) return;

        if (visionSuccesses == 0)
        {
            logger.LogWarning(
                "{Pass}: every vision call this cycle failed ({Count} attachment(s)); not counting toward " +
                "poison-document retirement — Ollama may be unable to run the vision model. Will retry next cycle.",
                pass, failedAttachmentIds.Count);
            return;
        }

        foreach (var id in failedAttachmentIds)
        {
            if (RecordVisionFailure(id))
            {
                logger.LogWarning(
                    "{Pass}: attachment {AttachmentId} failed {Max}x in cycles where other documents OCR'd fine; " +
                    "marking failed to unblock the queue.",
                    pass, id, MaxVisionAttempts);
                messages.MarkAttachmentOcrFailed(id);
            }
            else
            {
                logger.LogWarning("{Pass}: vision call failed for attachment {AttachmentId}; will retry next cycle.", pass, id);
            }
        }
    }

    /// <summary>
    /// OCR up to <paramref name="batchSize"/> scanned PDFs. Returns the number
    /// successfully OCR'd (0 when there's nothing to do, or the vision model
    /// isn't available — a logged, graceful skip).
    /// </summary>
    public async Task<int> ProcessBatchAsync(int batchSize, CancellationToken ct)
    {
        var candidates = messages.EnumerateAttachmentsNeedingOcr(Math.Max(1, batchSize));
        if (candidates.Count == 0) return 0;

        if (!await vision.IsModelAvailableAsync(ct).ConfigureAwait(false))
        {
            logger.LogWarning(
                "OCR is enabled but the vision model is unavailable; leaving {Count} scanned PDF(s) unprocessed. " +
                "Pull it (`ollama pull <Ollama:VisionModel>`) or set Embedder:OcrEnabled=false.",
                candidates.Count);
            return 0;
        }

        int done = 0;
        int visionSuccesses = 0;
        int consecutiveFailures = 0;
        var failedThisCycle = new List<long>();
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();

            byte[] pdf;
            try
            {
                pdf = reader.ReadBytes(c.ToMessage(), c.PartIndex);
            }
            catch (FileNotFoundException)
            {
                // Stale DB row — the .eml moved/deleted. Leave 'no_text'; an
                // indexer rescan reconciles. Not a permanent OCR failure.
                logger.LogInformation(
                    "OCR skip: Maildir file missing for attachment {AttachmentId} (message {MessageId}).",
                    c.AttachmentId, c.MessageId);
                continue;
            }

            int pages;
            try
            {
                pages = Math.Min(PdfRenderer.PageCount(pdf), _maxPages);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // PDFium can't open it -> permanently unreadable. Mark failed so
                // we don't re-select a poison PDF every cycle.
                logger.LogWarning(ex, "OCR: cannot open PDF for attachment {AttachmentId}; marking failed.", c.AttachmentId);
                messages.MarkAttachmentOcrFailed(c.AttachmentId);
                continue;
            }

            var sb = new StringBuilder();
            try
            {
                for (int page = 0; page < pages; page++)
                {
                    ct.ThrowIfCancellationRequested();
                    var image = PdfRenderer.RenderPageJpeg(pdf, page);
                    var pageText = await vision.OcrAsync(image, ct).ConfigureAwait(false);
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append(pageText);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // genuine shutdown — propagate so the worker stops
            }
            catch (Exception ex)
            {
                // Transient until proven otherwise: Ollama down, or an HTTP
                // timeout (which surfaces as TaskCanceledException — an
                // OperationCanceledException — while ct is NOT cancelled).
                // Record the failure and move on to the NEXT candidate:
                // whether this cycle produces any successes is what decides
                // (in SettleVisionFailures) if these failures count toward
                // poison-document retirement or get written off as an Ollama
                // outage. Repeated consecutive failures mean the model likely
                // can't run at all — stop burning a timeout per candidate.
                failedThisCycle.Add(c.AttachmentId);
                if (++consecutiveFailures >= MaxConsecutiveCycleFailures)
                {
                    logger.LogWarning(ex,
                        "OCR: {Count} consecutive vision failures (last: attachment {AttachmentId}); aborting OCR batch this cycle.",
                        consecutiveFailures, c.AttachmentId);
                    break;
                }
                logger.LogWarning(ex,
                    "OCR: vision call failed for attachment {AttachmentId}; trying the next candidate.", c.AttachmentId);
                continue;
            }

            messages.SaveOcrText(c.AttachmentId, c.MessageId, sb.ToString());
            _visionFailures.Remove(c.AttachmentId);
            done++;
            visionSuccesses++;
            consecutiveFailures = 0;
            logger.LogInformation(
                "OCR'd attachment {AttachmentId} ({Pages} page(s), {Chars} chars); re-queued message {MessageId}.",
                c.AttachmentId, pages, sb.Length, c.MessageId);
        }

        SettleVisionFailures(failedThisCycle, visionSuccesses, "OCR");
        return done;
    }

    /// <summary>
    /// OCR up to <paramref name="batchSize"/> image attachments stuck at
    /// 'unsupported'. Stage-1 (byte) gating happens in the SQL candidate query;
    /// this method applies stage-2 (decode dimensions / aspect ratio) before the
    /// vision call. Returns the number that gained searchable text. Mirrors
    /// <see cref="ProcessBatchAsync"/>'s error handling — same graceful skip when
    /// the model is unavailable, same per-item terminal marking so a poison row
    /// never re-selects.
    /// </summary>
    public async Task<int> ProcessImageBatchAsync(int batchSize, CancellationToken ct)
    {
        var candidates = messages.EnumerateImagesNeedingOcr(Math.Max(1, batchSize), _opts.ImageOcrMinBytes);
        if (candidates.Count == 0) return 0;

        if (!await vision.IsModelAvailableAsync(ct).ConfigureAwait(false))
        {
            logger.LogWarning(
                "Image OCR is enabled but the vision model is unavailable; leaving {Count} image(s) unprocessed. " +
                "Pull it (`ollama pull <Ollama:VisionModel>`) or set Embedder:ImageOcrEnabled=false.",
                candidates.Count);
            return 0;
        }

        int done = 0;
        int visionSuccesses = 0;
        int consecutiveFailures = 0;
        var failedThisCycle = new List<long>();
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();

            byte[] bytes;
            try
            {
                bytes = reader.ReadBytes(c.ToMessage(), c.PartIndex);
            }
            catch (FileNotFoundException)
            {
                logger.LogInformation(
                    "Image OCR skip: Maildir file missing for attachment {AttachmentId} (message {MessageId}).",
                    c.AttachmentId, c.MessageId);
                continue;
            }

            // Decode + normalise. Null = not a decodable image (e.g. HEIC without
            // a codec, or a mislabeled binary): mark failed so it isn't retried.
            var normalized = ImageRenderer.TryNormalize(bytes);
            if (normalized is null)
            {
                logger.LogInformation(
                    "Image OCR: attachment {AttachmentId} did not decode as an image; marking failed.", c.AttachmentId);
                messages.MarkAttachmentOcrFailed(c.AttachmentId);
                continue;
            }

            // Stage-2 gate: icons/avatars (too small) and banner strips/spacers
            // (extreme aspect) carry no readable text. Terminally 'no_text' so the
            // queue drains instead of re-decoding them every cycle.
            int shortEdge = Math.Min(normalized.Width, normalized.Height);
            int longEdge = Math.Max(normalized.Width, normalized.Height);
            double aspect = shortEdge == 0 ? double.PositiveInfinity : (double)longEdge / shortEdge;
            if (shortEdge < _opts.ImageOcrMinDimension || aspect > _opts.ImageOcrMaxAspectRatio)
            {
                logger.LogInformation(
                    "Image OCR gate: attachment {AttachmentId} {W}x{H} (short {Short}px, aspect {Aspect:F1}) — skipping as non-content.",
                    c.AttachmentId, normalized.Width, normalized.Height, shortEdge, aspect);
                messages.MarkAttachmentImageNoText(c.AttachmentId);
                continue;
            }

            string text;
            try
            {
                text = await vision.OcrImageAsync(normalized.Jpeg, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // genuine shutdown — propagate so the worker stops
            }
            catch (Exception ex)
            {
                // Same failure policy as ProcessBatchAsync: record, move to
                // the next candidate, and let SettleVisionFailures decide at
                // end of cycle whether these count toward retirement (only
                // when another call succeeded this cycle, proving the model
                // can run) or get written off as an Ollama outage.
                failedThisCycle.Add(c.AttachmentId);
                if (++consecutiveFailures >= MaxConsecutiveCycleFailures)
                {
                    logger.LogWarning(ex,
                        "Image OCR: {Count} consecutive vision failures (last: attachment {AttachmentId}); aborting image OCR batch this cycle.",
                        consecutiveFailures, c.AttachmentId);
                    break;
                }
                logger.LogWarning(ex,
                    "Image OCR: vision call failed for attachment {AttachmentId}; trying the next candidate.", c.AttachmentId);
                continue;
            }

            // The vision call succeeded — that's model-health evidence even
            // when the transcription is empty.
            visionSuccesses++;
            consecutiveFailures = 0;
            _visionFailures.Remove(c.AttachmentId);

            // Empty transcription (a photo with no legible text) is the common
            // case here — mark terminal rather than persisting an empty 'ocr' row.
            if (string.IsNullOrWhiteSpace(text))
            {
                messages.MarkAttachmentImageNoText(c.AttachmentId);
                logger.LogInformation(
                    "Image OCR: attachment {AttachmentId} produced no text; marked no_text.", c.AttachmentId);
                continue;
            }

            messages.SaveOcrText(c.AttachmentId, c.MessageId, text);
            done++;
            logger.LogInformation(
                "OCR'd image attachment {AttachmentId} ({W}x{H}, {Chars} chars); re-queued message {MessageId}.",
                c.AttachmentId, normalized.Width, normalized.Height, text.Length, c.MessageId);
        }

        SettleVisionFailures(failedThisCycle, visionSuccesses, "Image OCR");
        return done;
    }
}
