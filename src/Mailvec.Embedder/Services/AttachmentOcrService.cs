using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Vision;
using Mailvec.Pdf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

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
    ILogger<AttachmentOcrService> logger,
    // Batch-outcome record. Optional so the existing tests (which build this
    // service by hand) keep compiling; null simply means the outcome keys go
    // unwritten, which reads downstream as "unknown" rather than "broken".
    MetadataRepository? metadata = null)
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
    // IMPORTANT: failures only COUNT toward retirement in cycles with
    // evidence the model can run — a successful vision call (page-level for
    // PDFs), or the tiny-image health probe that fires when a cycle ends
    // with failures but zero successes (see SettleVisionFailures /
    // ProbeVisionHealthAsync). The model-availability probe gating the batch
    // is a /api/tags name check, which answers 200 even when Ollama can't
    // actually load the model (GPU OOM, dead runner) — in that wedged state
    // every call times out, and counting those failures would permanently
    // retire perfectly good scans, one head-of-queue document at a time, for
    // as long as the outage lasted. Same-cycle success evidence is what
    // distinguishes "this document is poison" from "the model can't run at
    // all" — and the health probe supplies it even when the id-ordered
    // candidate window happens to lead with nothing but poison docs, which
    // otherwise aborts every cycle evidence-free and wedges the queue.
    private const int MaxVisionAttempts = 5;

    // How many consecutive vision failures within one cycle before we stop
    // trying further candidates (the model is likely wedged; back off until
    // the next poll rather than burning a timeout per candidate).
    private const int MaxConsecutiveCycleFailures = 2;

    // Keyed by DOCUMENT identity, not attachment id.
    //
    // attachments.id is an INTEGER PRIMARY KEY without AUTOINCREMENT, so a row
    // deleted from the tail of the rowid space hands its id to the next insert
    // — possibly one belonging to a different message. The database write-backs
    // have always guarded on the full identity for exactly this reason; this
    // in-memory counter did not, so a replacement document inherited the old
    // one's strikes and could be retired to 'failed' on its first vision error.
    // The key mirrors OcrIdentityMatch: same row, same message, same part, and
    // the parent's content_hash unmoved.
    private readonly Dictionary<OcrFailureKey, int> _visionFailures = new();

    // Strikes only matter while a document is still pending, and a retirement
    // or a success removes its entry — but a candidate that stops being
    // selected (message deleted, re-extracted to a different status) leaves one
    // behind. Bound the map so a long-running embedder can't accumulate them
    // indefinitely; oldest-inserted entries go first, and losing a strike count
    // costs at most one extra OCR attempt.
    private const int MaxTrackedFailures = 4096;

    /// <summary>
    /// Value identity for a document under OCR — see <see cref="_visionFailures"/>.
    /// </summary>
    private readonly record struct OcrFailureKey(long AttachmentId, long MessageId, int PartIndex, string? ContentHash);

    private static OcrFailureKey KeyOf(OcrCandidate c) =>
        new(c.AttachmentId, c.MessageId, c.PartIndex, c.ContentHash);

    // Documents whose BYTES could not be read, and the cycle number at which
    // they become selectable again.
    //
    // A read failure skips the candidate WITHOUT changing its status, so an
    // unreadable row never leaves the candidate set on its own. A missing file
    // does self-heal (the indexer soft-deletes it and both queries filter
    // `m.deleted_at IS NULL`), but a file that EXISTS and won't read —
    // permissions, a bad sector, a half-mounted volume — does not. Backing
    // those off avoids burning every cycle's IO budget re-reading them: unlike
    // the vision path there is no retirement to 'failed' here, because
    // "unreadable right now" is not evidence about the document, and stamping a
    // whole volume's worth of attachments failed during an I/O outage would be
    // far worse than a slow queue.
    //
    // The backoff is an EFFICIENCY measure and must not be mistaken for the
    // liveness one — that is _pdfCursor / _imageCursor below. On its own the
    // backoff only steps past a blocked prefix until it expires: with batch
    // size N and backoff K, the pass works through N x K blockers in exactly K
    // cycles, by which point the first blocker is selectable again and refills
    // the batch. At the defaults that is 20 blockers (4 x 5) cycling forever in
    // front of candidate 21 — the same starvation the backoff was added to fix,
    // just at a higher threshold. Reachable by a permissions problem on one
    // folder or a partially mounted archive.
    private readonly Dictionary<OcrFailureKey, long> _readBackoffUntilCycle = new();
    private long _cycle;

    // The most recent classified failure, so the end-of-cycle outcome record can
    // name a CAUSE rather than just "something failed". Unclassified exceptions
    // stay Transient, matching how the batch loop treats them.
    private VisionFailureKind _lastFailureKind = VisionFailureKind.Transient;

    // Resume points into the id-ordered candidate set, one per pass — the
    // liveness guarantee. Each cycle takes the page strictly after the cursor
    // and leaves the cursor at the end of that page, so selection sweeps the id
    // space instead of restarting at the lowest id; an empty page means the
    // sweep reached the end and wraps to 0. Every candidate is therefore reached
    // within one sweep no matter how many unreadable rows precede it.
    //
    // In memory rather than persisted: the cursor only has to be monotonic
    // WITHIN a run, and a restart resuming from 0 costs at most one extra sweep.
    // Two cursors, not one, because the passes have independent candidate sets
    // and are independently enabled.
    private long _pdfCursor;
    private long _imageCursor;

    // Pass invocations to skip a document after a failed read. Counted in
    // invocations rather than polls because the PDF and image passes each
    // advance the clock — so at the default 30 s poll this is ~2.5 minutes with
    // one pass enabled and ~75 s with both. Either way: long enough to clear
    // the batch, short enough that a genuinely transient blip costs almost
    // nothing.
    private const int ReadBackoffCycles = 5;

    // Ceiling on the over-fetch below, so a pathological archive can't turn one
    // poll into an unbounded scan.
    private const int MaxOverFetch = 256;

    /// <summary>
    /// How many rows to ask for so that, after dropping the ones still backing
    /// off, a full batch of actually-attemptable candidates remains.
    /// </summary>
    private int FetchSize(int batchSize) =>
        Math.Min(MaxOverFetch, batchSize + _readBackoffUntilCycle.Count);

    /// <summary>
    /// Take the next batch of attemptable candidates after <paramref name="cursor"/>,
    /// wrapping to the start of the id space when the sweep runs off the end,
    /// and leave the cursor on the last row actually examined.
    /// </summary>
    /// <remarks>
    /// "Examined" is the precise word and the easy thing to get wrong. The
    /// cursor must advance past rows we attempted AND rows we skipped for
    /// backoff — a skipped row has had its turn this sweep — but NOT past rows
    /// the over-fetch returned and we never looked at, which would silently
    /// drop perfectly good candidates from the sweep entirely. So it lands on
    /// the last row the scan below touched before it filled the batch, not on
    /// the last row the query returned.
    ///
    /// One wrap per call at most. A second empty page means there are genuinely
    /// no candidates, and looping would spin.
    /// </remarks>
    private List<OcrCandidate> NextPage(
        Func<int, long, IReadOnlyList<OcrCandidate>> fetch, int batchSize, ref long cursor)
    {
        var page = fetch(FetchSize(batchSize), cursor);
        if (page.Count == 0 && cursor != 0)
        {
            cursor = 0;
            page = fetch(FetchSize(batchSize), 0);
        }
        if (page.Count == 0) return [];

        var attemptable = new List<OcrCandidate>(Math.Min(batchSize, page.Count));
        var examined = 0;
        foreach (var c in page)
        {
            if (attemptable.Count == batchSize) break;
            examined++;
            if (_readBackoffUntilCycle.TryGetValue(KeyOf(c), out var until) && until > _cycle) continue;
            attemptable.Add(c);
        }

        cursor = page[examined - 1].AttachmentId;
        return attemptable;
    }

    /// <summary>
    /// Records a failed byte read so this document steps aside for a while.
    /// </summary>
    private void RecordReadFailure(OcrCandidate candidate)
    {
        var key = KeyOf(candidate);
        if (_readBackoffUntilCycle.Count >= MaxTrackedFailures && !_readBackoffUntilCycle.ContainsKey(key))
        {
            var evict = _readBackoffUntilCycle.Keys.First();
            _readBackoffUntilCycle.Remove(evict);
        }
        _readBackoffUntilCycle[key] = _cycle + ReadBackoffCycles;
    }

    // Tiny blank JPEG for the zero-success health probe (see
    // ProbeVisionHealthAsync). Internal so tests can tell probe calls apart
    // from document calls by reference.
    internal static byte[] HealthProbeJpeg => _probeJpeg.Value;

    private static readonly Lazy<byte[]> _probeJpeg = new(() =>
    {
        using var bmp = new SKBitmap(48, 48);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    });

    /// <summary>
    /// Direct model-health check for cycles that produced vision failures but
    /// zero successes: OCR a tiny blank image. Success (any response, even
    /// empty — a blank image legitimately has no text) proves the model can
    /// run, so this cycle's failures are document-specific and may count
    /// toward retirement; failure means an Ollama outage and nothing counts.
    /// Without this, a batch whose leading candidates all fail deterministically
    /// aborts every cycle before any success can occur — zero evidence, zero
    /// strikes, and the queue wedges permanently behind the same head-of-line
    /// documents (candidates are id-ordered). Costs one vision call, and only
    /// on zero-success cycles.
    /// </summary>
    private async Task<bool> ProbeVisionHealthAsync(CancellationToken ct)
    {
        try
        {
            await vision.OcrImageAsync(HealthProbeJpeg, ct).ConfigureAwait(false);
            // Counted: a hosted provider bills this like any other page, so
            // omitting it made PagesSentTotal an understatement rather than the
            // spending gauge it is documented as.
            Increment(OcrHealthKeys.PagesSentTotal, 1);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine shutdown
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Vision health probe failed; treating this cycle's OCR failures as an Ollama outage (nothing retired).");
            return false;
        }
    }

    /// <summary>
    /// Record a COMMITTED text write. The distinction is the whole point: a
    /// stale write-back persisted nothing, so counting it would report progress
    /// that did not happen — the same reason `done` is only incremented on
    /// OcrWriteOutcome.Committed.
    /// </summary>
    private void RecordOcrSuccess()
    {
        RecordOcrDecision();
        if (metadata is null) return;
        try
        {
            metadata.Set(OcrHealthKeys.LastSuccessAt, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            metadata.Set(OcrHealthKeys.ConsecutiveFailures, "0");
            metadata.Set(OcrHealthKeys.LastFailureKind, "");
        }
        catch (Exception ex)
        {
            // Best-effort telemetry must never take down the pass that produced it.
            logger.LogWarning(ex, "Failed to record OCR success marker.");
        }
    }

    /// <summary>
    /// Record a committed terminal DECISION — text recovered, or a definitive
    /// "this document has none". Both are full round trips that removed a
    /// document from the queue, so both prove the pass is working; only the
    /// first is a product win. See OcrHealthKeys.LastDecisionAt.
    /// </summary>
    private void RecordOcrDecision()
    {
        if (metadata is null) return;
        try
        {
            metadata.Set(OcrHealthKeys.LastDecisionAt, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            metadata.Set(OcrHealthKeys.ConsecutiveFailures, "0");
            metadata.Set(OcrHealthKeys.LastFailureKind, "");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record OCR decision marker.");
        }
    }

    /// <summary>
    /// Record a failed vision call, carrying its <see cref="VisionFailureKind"/>
    /// — the field that turns "OCR isn't working" into something actionable:
    /// AuthFailed means fix the key, Backpressure means it will recover itself,
    /// DocumentFatal means one document was refused and the pass is otherwise
    /// healthy.
    /// </summary>
    private void RecordOcrFailure(VisionFailureKind kind)
    {
        if (metadata is null) return;
        try
        {
            var prior = metadata.Get(OcrHealthKeys.ConsecutiveFailures);
            var next = int.TryParse(prior, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n + 1 : 1;
            metadata.Set(OcrHealthKeys.ConsecutiveFailures, next.ToString(CultureInfo.InvariantCulture));
            metadata.Set(OcrHealthKeys.LastFailureAt, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            metadata.Set(OcrHealthKeys.LastFailureKind, kind.ToString());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record OCR failure marker.");
        }
    }

    /// <summary>Bump a cumulative counter (retirements, pages sent). Best-effort.</summary>
    private void Increment(string key, int by)
    {
        if (metadata is null || by <= 0) return;
        try
        {
            var prior = metadata.Get(key);
            var next = (long.TryParse(prior, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0) + by;
            metadata.Set(key, next.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to increment OCR counter {Key}.", key);
        }
    }

    /// <summary>
    /// Whether an OCR result is too short to be worth storing — treated exactly
    /// as if the model had returned nothing.
    /// </summary>
    /// <remarks>
    /// Empty is not the only worthless answer. A vision model reading a photo of
    /// physical objects returns a stray glyph or two, and without a floor that
    /// becomes searchable content on a document nothing will ever revisit
    /// (both terminal states are terminal). See EmbedderOptions.OcrMinTextChars
    /// for the real case that motivated this.
    /// </remarks>
    private bool BelowTextFloor(string? text) =>
        string.IsNullOrWhiteSpace(text) || text.Trim().Length < _opts.OcrMinTextChars;

    /// <summary>What the batch loop should do with a classified vision failure.</summary>
    private enum VisionFailureAction
    {
        /// <summary>Fall through to the historical path: strike-eligible, may retire.</summary>
        CountAsTransient,

        /// <summary>This document is done for; already marked. Move to the next candidate.</summary>
        SkipDocument,

        /// <summary>Stop the batch. Nothing counted, everything stays selectable.</summary>
        AbortBatch,
    }

    /// <summary>
    /// Route a vision failure by its declared cause.
    ///
    /// The historical design inferred cause from context — "did anything else
    /// succeed this cycle?" — which is sound for a local model whose only
    /// failure modes are a bad document or a dead runner. A hosted provider
    /// breaks that inference, and one case breaks it destructively:
    /// <see cref="VisionFailureKind.Backpressure"/>. A 429 arrives while other
    /// documents in the same cycle succeed, so the pass concludes the model is
    /// healthy, counts a strike, and after five throttled cycles stamps a
    /// perfectly good scan 'failed' — permanently, since nothing re-selects a
    /// failed row. Backpressure therefore aborts the batch and counts nothing;
    /// the pass runs again next poll, which IS the backoff.
    ///
    /// An unclassified exception falls through to the transient path, so a
    /// provider that forgets to classify something degrades to "retry it"
    /// rather than "destroy it".
    /// </summary>
    private VisionFailureAction ClassifyVisionFailure(Exception ex, OcrCandidate c, string pass)
    {
        if (ex is not VisionException vision)
        {
            _lastFailureKind = VisionFailureKind.Transient;
            return VisionFailureAction.CountAsTransient;
        }

        _lastFailureKind = vision.Kind;

        switch (vision.Kind)
        {
            case VisionFailureKind.Backpressure:
                logger.LogWarning(
                    "{Pass}: provider asked us to slow down (attachment {AttachmentId}); aborting this cycle's batch. " +
                    "Nothing retired — backpressure says nothing about the document.",
                    pass, c.AttachmentId);
                return VisionFailureAction.AbortBatch;

            case VisionFailureKind.AuthOrConfig:
                // Every call will fail identically until a human intervenes.
                // Retiring documents here would empty the queue for a reason
                // that has nothing to do with any of them.
                logger.LogError(ex,
                    "{Pass}: vision provider rejected our credentials or endpoint; aborting OCR until reconfigured. " +
                    "Nothing retired. Check Vision:Provider and its credentials.",
                    pass);
                return VisionFailureAction.AbortBatch;

            case VisionFailureKind.DocumentFatal:
                // Deterministic for this payload — retire it now rather than
                // burning MaxVisionAttempts cycles rediscovering that. Same
                // treatment a PDF PDFium cannot open already gets.
                logger.LogWarning(ex,
                    "{Pass}: provider refused attachment {AttachmentId} as unprocessable; marking failed.",
                    pass, c.AttachmentId);
                messages.MarkAttachmentOcrFailed(c);
                Increment(OcrHealthKeys.RetiredTotal, 1);
                return VisionFailureAction.SkipDocument;

            default:
                return VisionFailureAction.CountAsTransient;
        }
    }

    // Returns true when this exact document has failed enough counted times to retire.
    private bool RecordVisionFailure(OcrCandidate candidate)
    {
        var key = KeyOf(candidate);
        var n = _visionFailures.GetValueOrDefault(key) + 1;
        if (n >= MaxVisionAttempts) { _visionFailures.Remove(key); return true; }

        if (_visionFailures.Count >= MaxTrackedFailures && !_visionFailures.ContainsKey(key))
        {
            // Dictionary enumeration order is insertion order in practice for
            // an add-only map; either way, dropping an arbitrary entry costs at
            // most one extra OCR attempt for that document.
            var evict = _visionFailures.Keys.First();
            _visionFailures.Remove(evict);
        }

        _visionFailures[key] = n;
        return false;
    }

    /// <summary>
    /// End-of-cycle bookkeeping for vision failures. With evidence that the
    /// model can run — a successful vision call this cycle, or a passing
    /// health probe — failures are document-specific, so they count toward
    /// retirement (and hit 'failed' after <see cref="MaxVisionAttempts"/>
    /// counted cycles). Without that evidence we can't tell a poison document
    /// from a wedged Ollama, so nothing is counted and everything retries
    /// next cycle.
    /// </summary>
    private void SettleVisionFailures(IReadOnlyList<OcrCandidate> failed, bool visionHealthy, string pass)
    {
        if (failed.Count == 0) return;

        if (!visionHealthy)
        {
            logger.LogWarning(
                "{Pass}: every vision call this cycle failed, including the health probe ({Count} attachment(s)); " +
                "not counting toward poison-document retirement — Ollama can't run the vision model. Will retry next cycle.",
                pass, failed.Count);
            return;
        }

        // Carry the whole candidate, not just its id: the retirement write is
        // identity-guarded against the snapshot (see MessageRepository's
        // OcrIdentityMatch), so an id alone can no longer address a document.
        foreach (var c in failed)
        {
            if (RecordVisionFailure(c))
            {
                if (messages.MarkAttachmentOcrFailed(c) == OcrWriteOutcome.Stale)
                {
                    logger.LogInformation(
                        "{Pass}: attachment {AttachmentId} reached its retirement threshold but the row moved; " +
                        "nothing retired.",
                        pass, c.AttachmentId);
                }
                else
                {
                    Increment(OcrHealthKeys.RetiredTotal, 1);
                    logger.LogWarning(
                        "{Pass}: attachment {AttachmentId} failed {Max}x in cycles where other documents OCR'd fine; " +
                        "marked failed to unblock the queue.",
                        pass, c.AttachmentId, MaxVisionAttempts);
                }
            }
            else
            {
                logger.LogWarning("{Pass}: vision call failed for attachment {AttachmentId}; will retry next cycle.", pass, c.AttachmentId);
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
        batchSize = Math.Max(1, batchSize);
        _cycle++;
        // Over-fetch, then drop anything still backing off from a failed read,
        // so unreadable rows at the front of the id order can't fill the batch
        // and starve everything behind them. See _readBackoffUntilCycle.
        var candidates = NextPage(
            (limit, after) => messages.EnumerateAttachmentsNeedingOcr(limit, after), batchSize, ref _pdfCursor);
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
        int pagesSent = 0;
        // Any vision failure this cycle, INCLUDING the kinds that abort the
        // batch. failedThisCycle alone is not enough: AuthOrConfig and
        // Backpressure break out before a candidate is added to it, so keying
        // the outcome record off that list would leave a completely broken
        // provider (bad key) writing no failure at all — the report would show
        // an ageing last-success and no reason, which is the visibility hole
        // this record exists to close.
        bool sawFailure = false;
        var failedThisCycle = new List<OcrCandidate>();
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();

            byte[] pdf;
            try
            {
                pdf = reader.ReadBytes(c.ToMessage(), c.PartIndex, _opts.OcrMaxAttachmentBytes);
            }
            catch (FileNotFoundException)
            {
                // Stale DB row — the .eml moved/deleted. Leave 'no_text'; an
                // indexer rescan reconciles (it soft-deletes the message, and
                // the candidate query filters deleted_at IS NULL). Not a
                // permanent OCR failure. Back it off anyway: reconciliation is
                // a whole scan away, and until then this row would otherwise
                // hold its place at the front of every batch.
                RecordReadFailure(c);
                logger.LogInformation(
                    "OCR skip: Maildir file missing for attachment {AttachmentId} (message {MessageId}).",
                    c.AttachmentId, c.MessageId);
                continue;
            }
            catch (IOException ex)
            {
                // Possibly transient (permissions blip, volume hiccup), and
                // possibly not — a bad sector reads the same way forever. Back
                // off instead of retrying every cycle: the file EXISTS, so
                // nothing else ever removes it from the candidate set, and at
                // the default batch of 4 a handful of these would otherwise
                // starve every valid candidate behind them indefinitely.
                RecordReadFailure(c);
                logger.LogWarning(ex,
                    "OCR skip: could not read Maildir file for attachment {AttachmentId}; will retry next cycle.",
                    c.AttachmentId);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Deterministic for this file+part: a corrupt .eml (MimeKit
                // FormatException) or a stale part_index (ArgumentOutOfRange)
                // fails identically on every read. Before this catch existed,
                // the exception aborted BOTH OCR passes for the cycle, and the
                // id-ordered candidate query re-selected the same poison row
                // first every cycle — stalling the whole queue indefinitely.
                // Mark failed to retire it.
                logger.LogWarning(ex,
                    "OCR: cannot read attachment {AttachmentId} from its .eml (message {MessageId}); marking failed.",
                    c.AttachmentId, c.MessageId);
                messages.MarkAttachmentOcrFailed(c);
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
                messages.MarkAttachmentOcrFailed(c);
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
                    // Each successful page-level call is model-health evidence.
                    // Count it here, not on document completion: a multi-page
                    // doc that deterministically fails on a later page would
                    // otherwise contribute zero successes per cycle and retry
                    // forever, burning every earlier page's vision call each
                    // time without ever accruing a retirement strike.
                    visionSuccesses++;
                    consecutiveFailures = 0;
                    pagesSent++;
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
                sawFailure = true;
                var outcome = ClassifyVisionFailure(ex, c, "OCR");
                if (outcome == VisionFailureAction.AbortBatch) break;
                if (outcome == VisionFailureAction.SkipDocument) continue;

                // Transient until proven otherwise: Ollama down, or an HTTP
                // timeout (which surfaces as TaskCanceledException — an
                // OperationCanceledException — while ct is NOT cancelled).
                // Record the failure and move on to the NEXT candidate:
                // whether this cycle produces any successes is what decides
                // (in SettleVisionFailures) if these failures count toward
                // poison-document retirement or get written off as an Ollama
                // outage. Repeated consecutive failures mean the model likely
                // can't run at all — stop burning a timeout per candidate.
                failedThisCycle.Add(c);
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

            // Only a COMMITTED write is progress. A stale result means the row
            // moved between selection and write-back (minutes of page renders
            // and vision calls in between) — nothing was written, so counting
            // it would inflate `done`, tell the worker OCR produced work (an
            // immediate extra poll for nothing), and clear failure strikes that
            // belong to a document we never actually transcribed.
            // Apply the same floor as the image pass. SaveOcrText already treats
            // blank text as the terminal "OCR'd it, got nothing" marker — it
            // commits the status but skips the attachment_text rebuild and the
            // re-embed — so collapsing an under-floor result to empty reuses
            // that path exactly rather than inventing a second one.
            var pdfText = BelowTextFloor(sb.ToString()) ? string.Empty : sb.ToString();
            if (pdfText.Length == 0 && sb.Length > 0)
            {
                logger.LogInformation(
                    "OCR: attachment {AttachmentId} produced only {Chars} chars (floor {Floor}); storing no text.",
                    c.AttachmentId, sb.ToString().Trim().Length, _opts.OcrMinTextChars);
            }

            if (messages.SaveOcrText(c, pdfText) == OcrWriteOutcome.Stale)
            {
                logger.LogInformation(
                    "OCR: attachment {AttachmentId} changed underneath the OCR pass; discarding the transcription. " +
                    "The replacement is re-selected next cycle against its own snapshot.",
                    c.AttachmentId);
                continue;
            }

            _visionFailures.Remove(KeyOf(c));
            done++;
            // Blank text is a committed terminal decision but not a text
            // recovery, so it counts for liveness and not for the product
            // metric.
            if (pdfText.Length == 0) RecordOcrDecision(); else RecordOcrSuccess();
            logger.LogInformation(
                "OCR'd attachment {AttachmentId} ({Pages} page(s), {Chars} chars); re-queued message {MessageId}.",
                c.AttachmentId, pages, sb.Length, c.MessageId);
        }

        Increment(OcrHealthKeys.PagesSentTotal, pagesSent);

        bool visionHealthy = visionSuccesses > 0
            || (failedThisCycle.Count > 0 && await ProbeVisionHealthAsync(ct).ConfigureAwait(false));
        SettleVisionFailures(failedThisCycle, visionHealthy, "OCR");

        // Record an outcome only for a cycle that actually attempted work. An
        // idle cycle (nothing pending) must not write a failure, or a drained
        // queue would report as broken — the exact ambiguity these keys exist
        // to remove.
        if (sawFailure && done == 0) RecordOcrFailure(_lastFailureKind);
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
        batchSize = Math.Max(1, batchSize);
        // Advance here too, not just in the PDF pass: the two passes are gated
        // independently (Embedder:OcrEnabled / Embedder:ImageOcrEnabled), so a
        // deployment running images-only would otherwise never move the clock
        // and every read backoff would become permanent.
        _cycle++;
        var candidates = NextPage(
            (limit, after) => messages.EnumerateImagesNeedingOcr(limit, _opts.ImageOcrMinBytes, after),
            batchSize, ref _imageCursor);
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
        int pagesSent = 0;
        // Any vision failure this cycle, INCLUDING the kinds that abort the
        // batch. failedThisCycle alone is not enough: AuthOrConfig and
        // Backpressure break out before a candidate is added to it, so keying
        // the outcome record off that list would leave a completely broken
        // provider (bad key) writing no failure at all — the report would show
        // an ageing last-success and no reason, which is the visibility hole
        // this record exists to close.
        bool sawFailure = false;
        var failedThisCycle = new List<OcrCandidate>();
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();

            byte[] bytes;
            try
            {
                bytes = reader.ReadBytes(c.ToMessage(), c.PartIndex, _opts.OcrMaxAttachmentBytes);
            }
            catch (FileNotFoundException)
            {
                // Backed off for the same reason as the PDF pass: a stale row
                // self-heals only when an indexer rescan soft-deletes the
                // message, which is a whole scan away, and until then this row
                // holds its place at the front of every batch.
                RecordReadFailure(c);
                logger.LogInformation(
                    "Image OCR skip: Maildir file missing for attachment {AttachmentId} (message {MessageId}).",
                    c.AttachmentId, c.MessageId);
                continue;
            }
            catch (IOException ex)
            {
                // Same tiering as ProcessBatchAsync: possibly transient → skip
                // and retry after a backoff, without blocking the rest of the
                // batch. The backoff is not optional here. This pass already
                // HONOURS _readBackoffUntilCycle through SelectAttemptable, but
                // for a while it recorded nothing into it, so an unreadable
                // image was re-selected every cycle: the candidate query is
                // `ORDER BY a.id LIMIT`, the row's status never changes on a
                // failed read, and FetchSize only over-fetches by the number of
                // RECORDED backoffs — so at the default batch of 4, four
                // low-id unreadable images filled every batch and starved every
                // valid image behind them permanently and silently. A file that
                // EXISTS and won't read never leaves the candidate set on its
                // own. Test: Unreadable_images_do_not_starve_a_valid_one_behind_them.
                RecordReadFailure(c);
                logger.LogWarning(ex,
                    "Image OCR skip: could not read Maildir file for attachment {AttachmentId}; will retry next cycle.",
                    c.AttachmentId);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Deterministic read/parse failure (corrupt .eml, stale
                // part_index) — retire it; see ProcessBatchAsync.
                logger.LogWarning(ex,
                    "Image OCR: cannot read attachment {AttachmentId} from its .eml (message {MessageId}); marking failed.",
                    c.AttachmentId, c.MessageId);
                messages.MarkAttachmentOcrFailed(c);
                continue;
            }

            // Decode + normalise. Null = not a decodable image (e.g. HEIC without
            // a codec, or a mislabeled binary): mark failed so it isn't retried.
            var normalized = ImageRenderer.TryNormalize(bytes);
            if (normalized is null)
            {
                logger.LogInformation(
                    "Image OCR: attachment {AttachmentId} did not decode as an image; marking failed.", c.AttachmentId);
                messages.MarkAttachmentOcrFailed(c);
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
                messages.MarkAttachmentImageNoText(c);
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
                //
                // Classified first, for the same reasons as the PDF pass — and
                // this pass is the one a hosted provider hits hardest, since the
                // image backlog is an order of magnitude larger than the PDF one.
                sawFailure = true;
                var outcome = ClassifyVisionFailure(ex, c, "Image OCR");
                if (outcome == VisionFailureAction.AbortBatch) break;
                if (outcome == VisionFailureAction.SkipDocument) continue;

                failedThisCycle.Add(c);
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
            // when the transcription is empty. It is also a billed page on a
            // hosted provider whether or not it yielded text, so it counts here
            // rather than at the write-back.
            visionSuccesses++;
            consecutiveFailures = 0;
            pagesSent++;
            _visionFailures.Remove(KeyOf(c));

            // Empty transcription (a photo with no legible text) is the common
            // case here — mark terminal rather than persisting an empty 'ocr' row.
            // A result under the floor counts as empty: see BelowTextFloor.
            if (BelowTextFloor(text))
            {
                if (messages.MarkAttachmentImageNoText(c) == OcrWriteOutcome.Stale)
                {
                    logger.LogInformation(
                        "Image OCR: attachment {AttachmentId} changed underneath the pass; no_text mark skipped.",
                        c.AttachmentId);
                }
                else
                {
                    // A committed "no text here" is a working pass, not a
                    // non-event: provider called, answer received, document
                    // removed from the queue.
                    RecordOcrDecision();
                    logger.LogInformation(
                        "Image OCR: attachment {AttachmentId} produced no usable text ({Chars} chars, floor {Floor}); marked no_text.",
                        c.AttachmentId, text?.Trim().Length ?? 0, _opts.OcrMinTextChars);
                }
                continue;
            }

            if (messages.SaveOcrText(c, text) == OcrWriteOutcome.Stale)
            {
                logger.LogInformation(
                    "Image OCR: attachment {AttachmentId} changed underneath the OCR pass; discarding the transcription.",
                    c.AttachmentId);
                continue;
            }

            done++;
            RecordOcrSuccess();
            logger.LogInformation(
                "OCR'd image attachment {AttachmentId} ({W}x{H}, {Chars} chars); re-queued message {MessageId}.",
                c.AttachmentId, normalized.Width, normalized.Height, text.Length, c.MessageId);
        }

        Increment(OcrHealthKeys.PagesSentTotal, pagesSent);

        bool visionHealthy = visionSuccesses > 0
            || (failedThisCycle.Count > 0 && await ProbeVisionHealthAsync(ct).ConfigureAwait(false));
        SettleVisionFailures(failedThisCycle, visionHealthy, "Image OCR");

        // Record an outcome only for a cycle that actually attempted work. An
        // idle cycle (nothing pending) must not write a failure, or a drained
        // queue would report as broken — the exact ambiguity these keys exist
        // to remove.
        if (sawFailure && done == 0) RecordOcrFailure(_lastFailureKind);
        return done;
    }
}
