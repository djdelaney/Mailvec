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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Transient (Ollama down / timeout). Leave 'no_text' for retry and
                // stop the batch — no point hammering a wedged model this cycle.
                logger.LogWarning(ex,
                    "OCR: vision call failed for attachment {AttachmentId}; will retry. Aborting OCR batch.", c.AttachmentId);
                break;
            }

            messages.SaveOcrText(c.AttachmentId, c.MessageId, sb.ToString());
            done++;
            logger.LogInformation(
                "OCR'd attachment {AttachmentId} ({Pages} page(s), {Chars} chars); re-queued message {MessageId}.",
                c.AttachmentId, pages, sb.Length, c.MessageId);
        }

        return done;
    }
}
