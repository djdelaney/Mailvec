using System.Runtime.Versioning;
using System.Text;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Core.Vision;
using Mailvec.Embedder.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Mailvec.Embedder.Tests;

/// <summary>
/// The scanned-PDF OCR pass end to end: render (real PDFium) → fake vision OCR
/// → write back + re-queue. A blank PDF renders fine; the fake vision client
/// returns canned text regardless of pixels, so we test the pipeline without a
/// real Ollama. Platform-gated because the renderer is native.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("windows")]
public class AttachmentOcrServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _maildirRoot;
    private readonly ConnectionFactory _connections;
    private readonly MessageRepository _messages;

    public AttachmentOcrServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mailvec-ocr-svc-" + Guid.NewGuid().ToString("N"));
        _maildirRoot = Path.Combine(_root, "Mail");
        Directory.CreateDirectory(Path.Combine(_maildirRoot, "INBOX", "cur"));
        _connections = new ConnectionFactory(Options.Create(new ArchiveOptions
        {
            DatabasePath = Path.Combine(_root, "archive.sqlite"),
        }));
        new SchemaMigrator(_connections, NullLogger<SchemaMigrator>.Instance).EnsureUpToDate();
        _messages = new MessageRepository(_connections);
    }

    public void Dispose()
    {
        using (var conn = _connections.Open())
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(conn);
        }
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private AttachmentOcrService Build(IVisionClient vision, EmbedderOptions? opts = null) =>
        new(_messages,
            new MaildirAttachmentReader(Options.Create(new IngestOptions { MaildirRoot = _maildirRoot })),
            vision,
            Options.Create(opts ?? new EmbedderOptions()),
            NullLogger<AttachmentOcrService>.Instance);

    // Image tests use a tiny byte gate so a small generated PNG is still selected
    // by the SQL candidate query (the default gate is 50KB).
    private static EmbedderOptions ImageGate => new() { ImageOcrMinBytes = 1 };

    // partIndex normally 0 (the .eml written here has one attachment part);
    // pass a higher value to fabricate a stale DB row whose part doesn't
    // exist on disk — the Maildir read then throws ArgumentOutOfRange.
    [Fact]
    public async Task Strikes_do_not_carry_across_a_document_change_on_the_same_row()
    {
        // The failure counter used to be keyed by attachments.id alone, while
        // every DATABASE write-back was already identity-guarded for the same
        // reason: attachments.id is an INTEGER PRIMARY KEY without
        // AUTOINCREMENT, so a row deleted from the tail of the rowid space
        // hands its id to the next insert — possibly a different message's
        // document. The replacement inherited the strikes and could be retired
        // to 'failed' on its FIRST vision error, with nothing left to re-select
        // it. Changing the parent's content_hash reproduces the same identity
        // shift with far less setup: same row, different document.
        long poison = StageNoTextPdf("poison@x", MinimalPdf(1));
        var calls = 0;
        var svc = Build(new FakeVision(true, _ =>
            ++calls % 2 == 1 ? throw new TaskCanceledException("poison render hangs the model") : "GOOD TEXT"));

        // Four counted failures — one short of MaxVisionAttempts (5).
        for (int cycle = 0; cycle < 4; cycle++)
        {
            StageNoTextPdf($"fresh-{cycle}@x", MinimalPdf(1));
            await svc.ProcessBatchAsync(10, default);
        }
        StatusOf(poison).ShouldBe(AttachmentTextExtractor.StatusNoText, "precondition: not yet retired");

        // The document at that row changes — the .eml was rewritten, so the
        // parent's content_hash moves. Same attachment id, different bytes.
        ReviseContentHash(poison, "h-poison@x-v2");

        StageNoTextPdf("fresh-final@x", MinimalPdf(1));
        await svc.ProcessBatchAsync(10, default);

        // Keyed by identity, this is strike 1 of 5 for a new document, so it
        // stays pending. Keyed by row id it would have been strike 5 and
        // retired to 'failed' — unsearchable, with nothing to re-select it.
        StatusOf(poison).ShouldBe(AttachmentTextExtractor.StatusNoText,
            "a replacement document must not inherit the previous occupant's strikes");
    }

    /// <summary>Move a message's content_hash — what a post-ingest .eml rewrite does.</summary>
    private void ReviseContentHash(long messageId, string hash)
    {
        using var conn = _connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE messages SET content_hash = $h WHERE id = $id";
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.ExecuteNonQuery();
    }

    private long StageNoTextPdf(string id, byte[] pdfBytes, int partIndex = 0)
    {
        var b64 = Convert.ToBase64String(pdfBytes);
        var eml =
            "Message-ID: <" + id + ">\nFrom: a@x\nTo: b@x\nSubject: s\nMIME-Version: 1.0\n" +
            "Content-Type: multipart/mixed; boundary=\"outer\"\n\n" +
            "--outer\nContent-Type: text/plain; charset=utf-8\n\nbody\n" +
            "--outer\nContent-Type: application/pdf; name=\"scan.pdf\"\n" +
            "Content-Disposition: attachment; filename=\"scan.pdf\"\nContent-Transfer-Encoding: base64\n\n" +
            b64 + "\n--outer--\n";
        File.WriteAllText(Path.Combine(_maildirRoot, "INBOX", "cur", id + ".eml"), eml);

        var parsed = new ParsedMessage(
            MessageId: id, ThreadId: id, Subject: "s", FromAddress: "a@x", FromName: null,
            ToAddresses: [], CcAddresses: [], DateSent: DateTimeOffset.UtcNow, BodyText: "body",
            BodyHtml: null, RawHeaders: $"Message-ID: <{id}>\r\n", SizeBytes: 100, ContentHash: $"h-{id}",
            Attachments: [new ParsedAttachment(partIndex, "scan.pdf", "application/pdf", pdfBytes.LongLength,
                ExtractedText: null, ExtractionStatus: AttachmentTextExtractor.StatusNoText)]);
        return _messages.Upsert(parsed, "INBOX", "INBOX/cur", id + ".eml", DateTimeOffset.UtcNow);
    }

    private string? StatusOf(long messageId) => _messages.GetById(messageId)!.Attachments[0].ExtractionStatus;
    private string? TextOf(long messageId) => _messages.GetById(messageId)!.Attachments[0].ExtractedText;

    [Fact]
    public async Task Unreadable_candidates_do_not_starve_a_valid_one_behind_them()
    {
        // Both candidate queries are `ORDER BY a.id LIMIT batchSize`, and a
        // failed byte read skips the candidate WITHOUT changing its status —
        // so an unreadable row keeps its place at the front of the id order and
        // is re-selected every cycle. At the default batch of 4, four of them
        // fill the entire result set and everything behind is starved forever,
        // silently: no error, no status change, nothing to re-trigger.
        //
        // A MISSING file eventually self-heals (the indexer soft-deletes the
        // message and the query filters deleted_at IS NULL). A file that EXISTS
        // and won't read never leaves the set on its own, which is the case
        // this pins. Deleting the .eml is simply the cheapest way to make the
        // read throw; the fix is the backoff, which applies to both.
        long[] blockers =
        [
            StageNoTextPdf("block1@x", MinimalPdf(1)),
            StageNoTextPdf("block2@x", MinimalPdf(1)),
            StageNoTextPdf("block3@x", MinimalPdf(1)),
            StageNoTextPdf("block4@x", MinimalPdf(1)),
        ];
        // Staged last, so it sorts behind all four blockers by attachment id.
        long valid = StageNoTextPdf("zvalid@x", MinimalPdf(1));

        foreach (var id in new[] { "block1@x", "block2@x", "block3@x", "block4@x" })
            File.Delete(Path.Combine(_maildirRoot, "INBOX", "cur", id + ".eml"));

        var svc = Build(new FakeVision(available: true, ocr: _ => "RECOVERED"));

        // Batch of 4 — exactly filled by the four unreadable rows on cycle 1.
        var firstCycle = await svc.ProcessBatchAsync(4, default);
        firstCycle.ShouldBe(0, "all four selected candidates are unreadable");
        TextOf(valid).ShouldBeNull("the valid candidate is behind them in id order");

        // Cycle 2: the blockers are backing off, so the valid row is reachable.
        var secondCycle = await svc.ProcessBatchAsync(4, default);

        secondCycle.ShouldBe(1);
        TextOf(valid).ShouldBe("RECOVERED");
        StatusOf(valid).ShouldBe(AttachmentTextExtractor.StatusOcr);

        // And the unreadable rows are left alone — not retired to 'failed'.
        // "Unreadable right now" says nothing about the document, and stamping
        // a whole volume's worth of attachments during an I/O outage would be
        // far worse than a slow queue.
        foreach (var id in blockers)
            StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusNoText);
    }

    [Fact]
    public async Task Ocrs_a_scanned_pdf_and_writes_text_with_ocr_status()
    {
        long id = StageNoTextPdf("scan@x", MinimalPdf(1));

        var done = await Build(new FakeVision(available: true, ocr: _ => "RECOVERED TEXT")).ProcessBatchAsync(10, default);

        done.ShouldBe(1);
        TextOf(id).ShouldBe("RECOVERED TEXT");
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusOcr);
    }

    [Fact]
    public async Task Concatenates_text_across_pages()
    {
        long id = StageNoTextPdf("multi@x", MinimalPdf(3));

        await Build(new FakeVision(available: true, ocr: _ => "PAGE")).ProcessBatchAsync(10, default);

        TextOf(id).ShouldBe("PAGE\n\nPAGE\n\nPAGE");
    }

    [Fact]
    public async Task Skips_and_leaves_no_text_when_the_model_is_unavailable()
    {
        long id = StageNoTextPdf("scan@x", MinimalPdf(1));

        var done = await Build(new FakeVision(available: false, ocr: _ => "x")).ProcessBatchAsync(10, default);

        done.ShouldBe(0);
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusNoText); // untouched, retried later
    }

    [Fact]
    public async Task Marks_failed_when_the_pdf_cannot_be_opened()
    {
        long id = StageNoTextPdf("bad@x", Encoding.ASCII.GetBytes("this is not a pdf at all"));

        var done = await Build(new FakeVision(available: true, ocr: _ => "x")).ProcessBatchAsync(10, default);

        done.ShouldBe(0);
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusFailed); // poison PDF, not retried
    }

    // ── Image OCR pass (ProcessImageBatchAsync) ──────────────────────────────

    private long StageUnsupportedImage(string id, byte[] imageBytes, string contentType = "image/png")
    {
        var b64 = Convert.ToBase64String(imageBytes);
        var eml =
            "Message-ID: <" + id + ">\nFrom: a@x\nTo: b@x\nSubject: s\nMIME-Version: 1.0\n" +
            "Content-Type: multipart/mixed; boundary=\"outer\"\n\n" +
            "--outer\nContent-Type: text/plain; charset=utf-8\n\nbody\n" +
            "--outer\nContent-Type: " + contentType + "; name=\"photo.img\"\n" +
            "Content-Disposition: attachment; filename=\"photo.img\"\nContent-Transfer-Encoding: base64\n\n" +
            b64 + "\n--outer--\n";
        File.WriteAllText(Path.Combine(_maildirRoot, "INBOX", "cur", id + ".eml"), eml);

        var parsed = new ParsedMessage(
            MessageId: id, ThreadId: id, Subject: "s", FromAddress: "a@x", FromName: null,
            ToAddresses: [], CcAddresses: [], DateSent: DateTimeOffset.UtcNow, BodyText: "body",
            BodyHtml: null, RawHeaders: $"Message-ID: <{id}>\r\n", SizeBytes: 100, ContentHash: $"h-{id}",
            Attachments: [new ParsedAttachment(0, "photo.img", contentType, imageBytes.LongLength,
                ExtractedText: null, ExtractionStatus: AttachmentTextExtractor.StatusUnsupported)]);
        return _messages.Upsert(parsed, "INBOX", "INBOX/cur", id + ".eml", DateTimeOffset.UtcNow);
    }

    private static byte[] MakePng(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.CornflowerBlue);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task Ocrs_an_image_and_writes_text_with_ocr_status()
    {
        long id = StageUnsupportedImage("img@x", MakePng(300, 300));

        var done = await Build(new FakeVision(true, _ => "IMAGE TEXT"), ImageGate).ProcessImageBatchAsync(10, default);

        done.ShouldBe(1);
        TextOf(id).ShouldBe("IMAGE TEXT");
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusOcr);
    }

    [Fact]
    public async Task Unreadable_images_do_not_starve_a_valid_one_behind_them()
    {
        // The image pass HONOURED the shared read backoff through
        // SelectAttemptable but recorded nothing into it, so every unreadable
        // image was re-selected every cycle — the same starvation
        // Unreadable_candidates_do_not_starve_a_valid_one_behind_them pins for
        // the PDF pass, in a pass whose own comment claimed "same tiering as
        // ProcessBatchAsync". Nothing caught it because the two passes were
        // only ever tested apart, and the PDF pass's backoffs made the SHARED
        // dictionary look populated from the image pass's side.
        //
        // Deleting the .eml is the cheapest way to make the read throw; the
        // starvation is worse for a file that exists and won't read, since
        // nothing ever removes that one from the candidate set.
        long[] blockers =
        [
            StageUnsupportedImage("block1@x", MakePng(300, 300)),
            StageUnsupportedImage("block2@x", MakePng(300, 300)),
            StageUnsupportedImage("block3@x", MakePng(300, 300)),
            StageUnsupportedImage("block4@x", MakePng(300, 300)),
        ];
        // Staged last, so it sorts behind all four blockers by attachment id.
        long valid = StageUnsupportedImage("zvalid@x", MakePng(300, 300));

        foreach (var id in new[] { "block1@x", "block2@x", "block3@x", "block4@x" })
            File.Delete(Path.Combine(_maildirRoot, "INBOX", "cur", id + ".eml"));

        var svc = Build(new FakeVision(available: true, ocr: _ => "IMAGE TEXT"), ImageGate);

        var firstCycle = await svc.ProcessImageBatchAsync(4, default);
        firstCycle.ShouldBe(0, "all four selected candidates are unreadable");
        TextOf(valid).ShouldBeNull("the valid candidate is behind them in id order");

        var secondCycle = await svc.ProcessImageBatchAsync(4, default);

        secondCycle.ShouldBe(1);
        TextOf(valid).ShouldBe("IMAGE TEXT");
        StatusOf(valid).ShouldBe(AttachmentTextExtractor.StatusOcr);

        // Backed off, not retired: "unreadable right now" is not evidence about
        // the image, and an I/O outage must not stamp a volume's worth of
        // attachments terminally.
        foreach (var id in blockers)
            StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusUnsupported);
    }

    [Fact]
    public async Task Image_model_unavailable_leaves_it_unsupported()
    {
        long id = StageUnsupportedImage("img@x", MakePng(300, 300));

        var done = await Build(new FakeVision(false, _ => "x"), ImageGate).ProcessImageBatchAsync(10, default);

        done.ShouldBe(0);
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusUnsupported); // retried later
    }

    [Fact]
    public async Task Marks_failed_when_the_image_cannot_be_decoded()
    {
        long id = StageUnsupportedImage("bad@x", Encoding.ASCII.GetBytes("this is not an image, just bytes past the tiny byte gate"));

        var done = await Build(new FakeVision(true, _ => "x"), ImageGate).ProcessImageBatchAsync(10, default);

        done.ShouldBe(0);
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusFailed); // undecodable, not retried
    }

    [Fact]
    public async Task Gates_out_a_too_small_image_as_no_text()
    {
        long id = StageUnsupportedImage("tiny@x", MakePng(100, 100)); // < 200px min dimension

        var done = await Build(new FakeVision(true, _ => "SHOULD NOT BE CALLED"), ImageGate).ProcessImageBatchAsync(10, default);

        done.ShouldBe(0);
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusNoText); // gated, not OCR'd
        TextOf(id).ShouldBeNull();
    }

    [Fact]
    public async Task Gates_out_an_extreme_aspect_image_as_no_text()
    {
        long id = StageUnsupportedImage("banner@x", MakePng(2000, 210)); // aspect 9.5 > 8

        var done = await Build(new FakeVision(true, _ => "x"), ImageGate).ProcessImageBatchAsync(10, default);

        done.ShouldBe(0);
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusNoText);
    }

    [Fact]
    public async Task Empty_transcription_marks_the_image_no_text()
    {
        long id = StageUnsupportedImage("blank@x", MakePng(300, 300));

        var done = await Build(new FakeVision(true, _ => "   "), ImageGate).ProcessImageBatchAsync(10, default);

        done.ShouldBe(0);
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusNoText); // no text found, not an empty 'ocr' row
    }

    [Fact]
    public async Task Vision_timeout_is_transient_leaving_the_image_for_retry()
    {
        long id = StageUnsupportedImage("slow@x", MakePng(300, 300));
        // An HTTP timeout surfaces as TaskCanceledException (an OperationCanceledException)
        // while the caller's token is NOT cancelled. It must be treated as transient —
        // batch aborts, image left 'unsupported', nothing thrown to the worker.
        var svc = Build(new FakeVision(true, _ => throw new TaskCanceledException("HttpClient.Timeout")), ImageGate);

        var done = await svc.ProcessImageBatchAsync(10, default);

        done.ShouldBe(0);
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusUnsupported); // NOT failed
    }

    [Fact]
    public async Task Wedged_ollama_never_retires_documents_no_matter_how_many_cycles()
    {
        // /api/tags answers 200 even when Ollama can't actually load the model
        // (GPU OOM, dead runner), so the availability probe passes while every
        // vision call times out. Those failures must NOT count toward poison-
        // document retirement — an hours-long wedge used to permanently mark
        // perfectly good scans 'failed', one head-of-queue doc per few cycles.
        long a = StageNoTextPdf("wedge-a@x", MinimalPdf(1));
        long b = StageNoTextPdf("wedge-b@x", MinimalPdf(1));
        var svc = Build(new FakeVision(true, _ => throw new TaskCanceledException("HttpClient.Timeout")));

        for (int cycle = 0; cycle < 8; cycle++) // well past MaxVisionAttempts
        {
            (await svc.ProcessBatchAsync(10, default)).ShouldBe(0);
        }

        StatusOf(a).ShouldBe(AttachmentTextExtractor.StatusNoText); // untouched, retried when Ollama recovers
        StatusOf(b).ShouldBe(AttachmentTextExtractor.StatusNoText);
    }

    /// <summary>
    /// A vision client that fails one identifiable document and succeeds on
    /// everything else, including the health probe.
    /// </summary>
    /// <remarks>
    /// Keyed on the rendered page's width rather than call order, because
    /// selection order is no longer something a test may assume: the passes
    /// sweep the id space from a cursor, so which documents a given cycle picks
    /// up depends on where the previous cycle stopped. A fake that fails "the
    /// first call of each cycle" silently tests the scheduler instead of the
    /// behaviour it is named for.
    /// </remarks>
    /// <param name="failAboveWidth">
    /// Between the two page sizes the test stages. PdfRenderer rasterises at
    /// 150 DPI (downscale-only), so a 200pt MediaBox lands at ~417px and a
    /// 400pt one at ~833px; 600 sits between them, and the 48px health probe is
    /// far below both.
    /// </param>
    private static FakeVision FailsWidePages(int failAboveWidth = 600) =>
        new(available: true, ocr: jpeg =>
        {
            using var bmp = SKBitmap.Decode(jpeg);
            return bmp is not null && bmp.Width > failAboveWidth
                ? throw new TaskCanceledException("poison render hangs the model")
                : "GOOD TEXT";
        });

    [Fact]
    public async Task Poison_document_retires_after_repeated_failures_alongside_successes()
    {
        // A doc that fails every attempt while others OCR fine — proof the model
        // is healthy, so its failures count and it retires after
        // MaxVisionAttempts, stopping the per-attempt timeout cost. The healthy
        // doc staged behind it must not be held up waiting for that.
        //
        // Attempts are now once per SWEEP rather than once per cycle, which is
        // the deliberate consequence of the fairness cursor: nothing is selected
        // twice until everything has been selected once. Retirement is therefore
        // slower on a big backlog — acceptable, because the reason to retire
        // quickly was head-of-line blocking, and the cursor removes that.
        long poison = StageNoTextPdf("poison@x", MinimalPdf(1, size: 400));
        long healthy = StageNoTextPdf("healthy@x", MinimalPdf(1, size: 200));
        var svc = Build(FailsWidePages());

        // Cycle 1 takes both; afterwards the healthy doc is 'ocr' and leaves the
        // candidate set, so each later cycle runs off the end and wraps back to
        // the poison doc — one attempt per sweep, five sweeps to retirement.
        for (int cycle = 0; cycle < 5; cycle++)
        {
            await svc.ProcessBatchAsync(10, default);
        }

        StatusOf(healthy).ShouldBe(AttachmentTextExtractor.StatusOcr);
        TextOf(healthy).ShouldBe("GOOD TEXT");
        StatusOf(poison).ShouldBe(AttachmentTextExtractor.StatusFailed);
    }

    [Fact]
    public async Task A_blocked_prefix_longer_than_the_backoff_window_still_lets_later_pdfs_through()
    {
        // The read backoff alone does NOT establish eventual progress. With
        // batch size N and backoff K cycles, the pass clears N x K blockers in
        // exactly K cycles — by which point the first blocker is selectable
        // again and refills the batch, and candidate N x K + 1 is never reached.
        // At the defaults that threshold is 20, so 25 blockers wedge the queue
        // permanently: the earlier fix raised the starvation threshold rather
        // than removing it. The cursor is what removes it.
        //
        // 25 unreadable files that EXIST would be a permissions problem on one
        // folder or a partially mounted archive; deleting them is just the
        // cheapest way to make the read throw.
        var blockers = new List<long>();
        for (var i = 0; i < 25; i++)
            blockers.Add(StageNoTextPdf($"blk{i:D2}@x", MinimalPdf(1)));
        long valid = StageNoTextPdf("zvalid@x", MinimalPdf(1));   // staged last: highest id

        for (var i = 0; i < 25; i++)
            File.Delete(Path.Combine(_maildirRoot, "INBOX", "cur", $"blk{i:D2}@x.eml"));

        var svc = Build(new FakeVision(available: true, ocr: _ => "RECOVERED"));

        var done = 0;
        for (var cycle = 0; cycle < 15; cycle++)
            done += await svc.ProcessBatchAsync(4, default);   // the default batch size

        done.ShouldBe(1);
        TextOf(valid).ShouldBe("RECOVERED");
        StatusOf(valid).ShouldBe(AttachmentTextExtractor.StatusOcr);

        // Blockers are left pending, not retired: "unreadable right now" says
        // nothing about the document.
        foreach (var id in blockers)
            StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusNoText);
    }

    [Fact]
    public async Task A_blocked_prefix_longer_than_the_backoff_window_still_lets_later_images_through()
    {
        // Same guarantee on the image pass, which has its own cursor because the
        // two passes have independent candidate sets and are independently
        // enabled (Embedder:OcrEnabled / Embedder:ImageOcrEnabled).
        var blockers = new List<long>();
        for (var i = 0; i < 25; i++)
            blockers.Add(StageUnsupportedImage($"iblk{i:D2}@x", MakePng(300, 300)));
        long valid = StageUnsupportedImage("zvalidimg@x", MakePng(300, 300));

        for (var i = 0; i < 25; i++)
            File.Delete(Path.Combine(_maildirRoot, "INBOX", "cur", $"iblk{i:D2}@x.eml"));

        var svc = Build(new FakeVision(available: true, ocr: _ => "IMAGE TEXT"), ImageGate);

        var done = 0;
        for (var cycle = 0; cycle < 15; cycle++)
            done += await svc.ProcessImageBatchAsync(4, default);

        done.ShouldBe(1);
        TextOf(valid).ShouldBe("IMAGE TEXT");
        foreach (var id in blockers)
            StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusUnsupported);
    }

    [Fact]
    public async Task Both_passes_sweeping_together_still_reach_a_late_candidate()
    {
        // With both passes enabled the shared _cycle advances twice per poll, so
        // every read backoff expires in half the polls — which under the old
        // scheme dropped the starvation threshold from 20 blockers to about 10.
        // The cursors are per-pass and unaffected by the shared clock, so this
        // has to hold with both running.
        for (var i = 0; i < 12; i++) StageNoTextPdf($"pblk{i:D2}@x", MinimalPdf(1));
        for (var i = 0; i < 12; i++) StageUnsupportedImage($"iblk{i:D2}@x", MakePng(300, 300));
        long validPdf = StageNoTextPdf("zvalidpdf@x", MinimalPdf(1));
        long validImg = StageUnsupportedImage("zvalidimg@x", MakePng(300, 300));

        for (var i = 0; i < 12; i++)
        {
            File.Delete(Path.Combine(_maildirRoot, "INBOX", "cur", $"pblk{i:D2}@x.eml"));
            File.Delete(Path.Combine(_maildirRoot, "INBOX", "cur", $"iblk{i:D2}@x.eml"));
        }

        var svc = Build(new FakeVision(available: true, ocr: _ => "RECOVERED"), ImageGate);

        for (var cycle = 0; cycle < 15; cycle++)
        {
            await svc.ProcessBatchAsync(4, default);
            await svc.ProcessImageBatchAsync(4, default);
        }

        StatusOf(validPdf).ShouldBe(AttachmentTextExtractor.StatusOcr);
        StatusOf(validImg).ShouldBe(AttachmentTextExtractor.StatusOcr);
    }

    [Fact]
    public async Task Adjacent_poison_documents_retire_via_the_health_probe_and_unblock_the_queue()
    {
        // Two poison docs lead the id-ordered queue and both fail every cycle,
        // so the consecutive-failure abort fires before any document-level
        // success can happen. Without the health probe that meant zero
        // same-cycle evidence, zero strikes, and a permanently wedged queue —
        // the healthy doc behind them never ran. The probe (a tiny blank
        // image the fake distinguishes by reference) proves the model healthy,
        // so strikes accrue and both retire after MaxVisionAttempts cycles.
        long poisonA = StageNoTextPdf("poison-a@x", MinimalPdf(1));
        long poisonB = StageNoTextPdf("poison-b@x", MinimalPdf(1));
        long healthy = StageNoTextPdf("behind@x", MinimalPdf(1));
        // Each wedged cycle makes exactly two document calls (poisonA, poisonB)
        // before the consecutive-failure abort; the healthy doc is never
        // reached. 5 cycles x 2 = 10 failing document calls, then the healthy
        // doc's call succeeds. Probe calls are distinguished by reference and
        // don't advance the counter.
        var docCalls = 0;
        var svc = Build(new FakeVision(true, img =>
        {
            if (ReferenceEquals(img, AttachmentOcrService.HealthProbeJpeg)) return ""; // probe: model healthy
            return ++docCalls <= 10
                ? throw new TaskCanceledException("this document hangs the model")
                : "RECOVERED";
        }));

        for (int cycle = 0; cycle < 5; cycle++) // MaxVisionAttempts
        {
            (await svc.ProcessBatchAsync(10, default)).ShouldBe(0);
        }

        StatusOf(poisonA).ShouldBe(AttachmentTextExtractor.StatusFailed);
        StatusOf(poisonB).ShouldBe(AttachmentTextExtractor.StatusFailed);
        // With the head-of-line poisons retired, the next cycle reaches the
        // healthy doc that was starving behind them.
        (await svc.ProcessBatchAsync(10, default)).ShouldBe(1);
        StatusOf(healthy).ShouldBe(AttachmentTextExtractor.StatusOcr);
        TextOf(healthy).ShouldBe("RECOVERED");
    }

    [Fact]
    public async Task Multi_page_document_failing_on_a_later_page_retires_via_page_level_successes()
    {
        // A 3-page PDF whose last page deterministically times out. Successes
        // used to be counted per *document*, so this doc produced zero
        // evidence per cycle — never a strike, retried forever, burning two
        // good page renders + vision calls every cycle. Page-level counting
        // makes pages 1-2 the health evidence that lets page 3's failure
        // accrue strikes until retirement.
        long id = StageNoTextPdf("lastpage@x", MinimalPdf(3));
        var calls = 0;
        var svc = Build(new FakeVision(true, img =>
        {
            if (ReferenceEquals(img, AttachmentOcrService.HealthProbeJpeg)) return "";
            return ++calls % 3 == 0 ? throw new TaskCanceledException("page 3 hangs") : "PAGE";
        }));

        for (int cycle = 0; cycle < 5; cycle++) // MaxVisionAttempts
        {
            (await svc.ProcessBatchAsync(10, default)).ShouldBe(0);
        }

        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusFailed); // retired, queue unblocked
        TextOf(id).ShouldBeNull(); // partial page text was never persisted
    }

    [Fact]
    public async Task Unreadable_candidate_is_retired_and_does_not_block_the_batch()
    {
        // A DB row whose part_index doesn't exist in the .eml (stale row after
        // a post-ingest rewrite) throws from the Maildir read. Before the
        // tiered catch, that exception escaped ProcessBatchAsync entirely —
        // aborting BOTH OCR passes for the cycle — and the id-ordered
        // candidate query re-selected the same row first every cycle: a
        // permanent, silent stall of the whole OCR queue.
        long poison = StageNoTextPdf("stale-part@x", MinimalPdf(1), partIndex: 7);
        long healthy = StageNoTextPdf("fine@x", MinimalPdf(1));
        var svc = Build(new FakeVision(true, _ => "GOOD TEXT"));

        var done = await svc.ProcessBatchAsync(10, default);

        done.ShouldBe(1);
        StatusOf(poison).ShouldBe(AttachmentTextExtractor.StatusFailed); // retired immediately, not retried
        StatusOf(healthy).ShouldBe(AttachmentTextExtractor.StatusOcr);   // the queue behind it still drains
        TextOf(healthy).ShouldBe("GOOD TEXT");
    }

    [Fact]
    public async Task Real_cancellation_propagates()
    {
        long id = StageUnsupportedImage("cancel@x", MakePng(300, 300));
        using var cts = new CancellationTokenSource();
        // Cancel mid-call, then throw OCE: with the token cancelled this is a real
        // shutdown and must propagate (not be swallowed as transient).
        var svc = Build(new FakeVision(true, _ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); }), ImageGate);

        await Should.ThrowAsync<OperationCanceledException>(() => svc.ProcessImageBatchAsync(10, cts.Token));
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusUnsupported);
    }

    [Fact]
    public async Task Backpressure_never_retires_a_document_however_long_it_lasts()
    {
        // THE regression this taxonomy exists for. A hosted provider rate-limits
        // one document while others in the same cycle succeed; the pass therefore
        // sees same-cycle success evidence, concludes the model is healthy, and
        // — before VisionFailureKind — counted the 429 as a poison-document
        // strike. Five throttled cycles later a perfectly good scan is stamped
        // 'failed', permanently, because no candidate query ever re-selects a
        // failed row. A traffic spike silently destroyed documents.
        long throttled = StageNoTextPdf("throttled@x", MinimalPdf(1));

        var svc = Build(new FakeVision(true, img =>
        {
            // The health probe answering normally is the "model is healthy"
            // evidence that used to license counting the 429 as a strike — it
            // is exactly how a hosted outage differs from a poison document,
            // and exactly why rate limiting slipped through as the latter.
            if (ReferenceEquals(img, AttachmentOcrService.HealthProbeJpeg)) return "";
            throw new VisionException(VisionFailureKind.Backpressure, "429 Too Many Requests");
        }));

        for (var cycle = 0; cycle < 20; cycle++) // 4x MaxVisionAttempts
        {
            await svc.ProcessBatchAsync(10, default);
        }

        // Still selectable, still no_text — the queue drains once the throttling
        // stops, and nothing was lost.
        StatusOf(throttled).ShouldBe(AttachmentTextExtractor.StatusNoText);
        TextOf(throttled).ShouldBeNull();
    }

    [Fact]
    public async Task Backpressure_stops_the_batch_rather_than_hammering_the_next_candidate()
    {
        // Backpressure means "slow down", so the right response is to stop this
        // cycle — not to walk the rest of the batch into the same wall. The pass
        // re-runs every poll, which IS the backoff.
        StageNoTextPdf("a@x", MinimalPdf(1));
        StageNoTextPdf("b@x", MinimalPdf(1));
        StageNoTextPdf("c@x", MinimalPdf(1));

        var docCalls = 0;
        var svc = Build(new FakeVision(true, img =>
        {
            if (ReferenceEquals(img, AttachmentOcrService.HealthProbeJpeg)) return "";
            docCalls++;
            throw new VisionException(VisionFailureKind.Backpressure, "429");
        }));

        await svc.ProcessBatchAsync(10, default);

        docCalls.ShouldBe(1); // aborted on the first 429, not once per candidate
    }

    [Fact]
    public async Task Auth_failure_aborts_without_retiring_anything()
    {
        // A bad key or endpoint fails every call identically until a human fixes
        // it. Retiring documents would empty the queue for a reason that has
        // nothing to do with any of them — and 'failed' is not re-selectable.
        long id = StageNoTextPdf("authfail@x", MinimalPdf(1));
        var svc = Build(new FakeVision(true, img =>
        {
            if (ReferenceEquals(img, AttachmentOcrService.HealthProbeJpeg)) return "";
            throw new VisionException(VisionFailureKind.AuthOrConfig, "401 Unauthorized");
        }));

        for (var cycle = 0; cycle < 20; cycle++)
        {
            await svc.ProcessBatchAsync(10, default);
        }

        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusNoText); // untouched, still selectable
    }

    [Fact]
    public async Task Document_fatal_retires_immediately_without_burning_five_cycles()
    {
        // The provider looked at THIS payload and refused it (413/415/422).
        // Deterministic, so there is nothing to learn from four more attempts.
        long fatal = StageNoTextPdf("toobig@x", MinimalPdf(1));
        long behind = StageNoTextPdf("behind@x", MinimalPdf(1));

        var svc = Build(new FakeVision(true, img =>
        {
            if (ReferenceEquals(img, AttachmentOcrService.HealthProbeJpeg)) return "";
            return StatusOf(fatal) == AttachmentTextExtractor.StatusFailed
                ? "RECOVERED"
                : throw new VisionException(VisionFailureKind.DocumentFatal, "413 Payload Too Large");
        }));

        // ONE cycle, not MaxVisionAttempts.
        await svc.ProcessBatchAsync(10, default);

        StatusOf(fatal).ShouldBe(AttachmentTextExtractor.StatusFailed);
        // And it didn't take the rest of the batch down with it.
        StatusOf(behind).ShouldBe(AttachmentTextExtractor.StatusOcr);
    }

    [Fact]
    public async Task Unclassified_failures_still_take_the_transient_path()
    {
        // A provider that forgets to classify something must degrade to
        // "retry it", never to "destroy it" — so a plain exception keeps the
        // historical strike-then-retire behaviour rather than short-circuiting.
        long id = StageNoTextPdf("plain@x", MinimalPdf(1));
        var svc = Build(new FakeVision(true, img =>
        {
            if (ReferenceEquals(img, AttachmentOcrService.HealthProbeJpeg)) return "";
            throw new InvalidOperationException("something unclassified");
        }));

        for (var cycle = 0; cycle < 4; cycle++) // one short of MaxVisionAttempts
        {
            await svc.ProcessBatchAsync(10, default);
        }
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusNoText); // not yet retired

        await svc.ProcessBatchAsync(10, default); // the fifth
        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusFailed);
    }

    [Fact]
    public async Task Image_pass_honours_backpressure_too()
    {
        // The image backlog is an order of magnitude larger than the PDF one,
        // so this is the pass a hosted provider throttles hardest.
        long id = StageUnsupportedImage("throttled-img@x", MakePng(300, 300));
        var svc = Build(new FakeVision(true, img =>
        {
            if (ReferenceEquals(img, AttachmentOcrService.HealthProbeJpeg)) return "";
            throw new VisionException(VisionFailureKind.Backpressure, "429");
        }), ImageGate);

        for (var cycle = 0; cycle < 20; cycle++)
        {
            await svc.ProcessImageBatchAsync(10, default);
        }

        StatusOf(id).ShouldBe(AttachmentTextExtractor.StatusUnsupported); // still selectable
    }

    private sealed class FakeVision(bool available, Func<byte[], string> ocr) : IVisionClient
    {
        public Task<string> OcrAsync(byte[] image, CancellationToken ct = default) => Task.FromResult(ocr(image));
        public Task<string> OcrImageAsync(byte[] image, CancellationToken ct = default) => Task.FromResult(ocr(image));
        public Task<bool> IsModelAvailableAsync(CancellationToken ct = default) => Task.FromResult(available);
    }

    /// <summary>Minimal valid PDF with <paramref name="pages"/> blank pages (xref offsets computed).</summary>
    private static byte[] MinimalPdf(int pages, int size = 200)
    {
        var objects = new List<string>
        {
            "<</Type /Catalog /Pages 2 0 R>>",
            $"<</Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{3 + i} 0 R"))}] /Count {pages}>>",
        };
        for (int i = 0; i < pages; i++)
            objects.Add($"<</Type /Page /Parent 2 0 R /MediaBox [0 0 {size} {size}]>>");

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n").Append($"0 {objects.Count + 1}\n").Append("0000000000 65535 f \n");
        foreach (var off in offsets)
            sb.Append(off.ToString("D10") + " 00000 n \n");
        sb.Append($"trailer\n<</Size {objects.Count + 1} /Root 1 0 R>>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
