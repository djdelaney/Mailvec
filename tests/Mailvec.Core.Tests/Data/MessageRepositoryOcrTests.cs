using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Parsing;
using Mailvec.Core.Tests.Data;

namespace Mailvec.Core.Tests.Data;

/// <summary>
/// The MessageRepository surface the embedder's OCR pass uses: find scanned
/// PDFs, write recovered text back, and mark poison PDFs failed.
/// </summary>
public class MessageRepositoryOcrTests
{
    private static long Insert(MessageRepository repo, string id, string fileName, string? status,
        string folder = "INBOX", string contentType = "application/pdf", long size = 100)
    {
        var parsed = new ParsedMessage(
            MessageId: id, ThreadId: id, Subject: "s", FromAddress: "a@x", FromName: null,
            ToAddresses: [], CcAddresses: [], DateSent: DateTimeOffset.UtcNow, BodyText: "body",
            BodyHtml: null, RawHeaders: $"Message-ID: <{id}>\r\n", SizeBytes: 100, ContentHash: $"h-{id}",
            Attachments: [new ParsedAttachment(0, fileName, contentType, size, ExtractedText: null, ExtractionStatus: status)]);
        return repo.Upsert(parsed, folder, $"{folder}/cur", id + ".eml", DateTimeOffset.UtcNow);
    }

    /// <summary>Insert a message with no attachment rows — the inline-cid:-only shape the backfill targets.</summary>
    private static long InsertBare(MessageRepository repo, string id, string folder = "INBOX")
    {
        var parsed = new ParsedMessage(
            MessageId: id, ThreadId: id, Subject: "s", FromAddress: "a@x", FromName: null,
            ToAddresses: [], CcAddresses: [], DateSent: DateTimeOffset.UtcNow, BodyText: "body",
            BodyHtml: null, RawHeaders: $"Message-ID: <{id}>\r\n", SizeBytes: 100, ContentHash: $"h-{id}",
            Attachments: []);
        return repo.Upsert(parsed, folder, $"{folder}/cur", id + ".eml", DateTimeOffset.UtcNow);
    }

    [Fact]
    public void EnumerateAttachmentsNeedingOcr_returns_only_no_text_pdfs()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        Insert(repo, "scan@x", "scan.pdf", AttachmentTextExtractor.StatusNoText);
        Insert(repo, "done@x", "ok.pdf", AttachmentTextExtractor.StatusDone);          // already extracted
        Insert(repo, "img@x", "photo.png", AttachmentTextExtractor.StatusNoText, contentType: "image/png"); // not a PDF

        var pending = repo.EnumerateAttachmentsNeedingOcr(50);

        pending.Count.ShouldBe(1);
        pending[0].MessageIdHeader.ShouldBe("scan@x");
        pending[0].MaildirFilename.ShouldBe("scan@x.eml");
    }

    [Fact]
    public void EnumerateAttachmentsNeedingOcr_matches_pdf_content_type_without_pdf_filename()
    {
        // A scanned PDF sent as application/pdf with an empty/non-.pdf filename
        // still lands at 'no_text' and must be OCR'd — the filename-suffix gate
        // alone stranded these.
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        Insert(repo, "ct@x", "attachment", AttachmentTextExtractor.StatusNoText, contentType: "application/pdf");

        var pending = repo.EnumerateAttachmentsNeedingOcr(50);

        pending.Count.ShouldBe(1);
        pending[0].MessageIdHeader.ShouldBe("ct@x");
    }

    [Fact]
    public void EnumerateAttachmentsNeedingOcr_excludes_soft_deleted()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Insert(repo, "del@x", "scan.pdf", AttachmentTextExtractor.StatusNoText);
        repo.MarkDeleted([id], DateTimeOffset.UtcNow);

        repo.EnumerateAttachmentsNeedingOcr(50).ShouldBeEmpty();
    }

    [Fact]
    public void SaveOcrText_writes_text_with_ocr_status_and_requeues_the_message()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Insert(repo, "scan@x", "scan.pdf", AttachmentTextExtractor.StatusNoText);
        var attId = repo.GetById(id)!.Attachments[0].Id;

        // Pretend it was already embedded (body only) so we can see the re-queue.
        SetEmbeddedAt(db, id);

        repo.SaveOcrText(attId, id, "RECOVERED INVOICE TEXT 1234");

        var att = repo.GetById(id)!.Attachments[0];
        att.ExtractedText.ShouldBe("RECOVERED INVOICE TEXT 1234");
        att.ExtractionStatus.ShouldBe(AttachmentTextExtractor.StatusOcr);
        EmbeddedAt(db, id).ShouldBeNull(); // re-queued for embedding
    }

    [Fact]
    public void SaveOcrText_with_blank_text_marks_terminal_but_does_not_requeue()
    {
        // A blank scan: status='ocr' with empty text is the terminal marker
        // (the OCR pass stops re-selecting it), but there's nothing new to
        // search, so the message must NOT be re-queued for a no-op re-embed.
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Insert(repo, "blank@x", "blank.pdf", AttachmentTextExtractor.StatusNoText);
        var attId = repo.GetById(id)!.Attachments[0].Id;
        SetEmbeddedAt(db, id);

        repo.SaveOcrText(attId, id, "   ");

        var att = repo.GetById(id)!.Attachments[0];
        att.ExtractionStatus.ShouldBe(AttachmentTextExtractor.StatusOcr);   // terminal, not re-selected
        EmbeddedAt(db, id).ShouldNotBeNull();                               // no re-embed churn
    }

    [Fact]
    public void SaveOcrText_makes_the_recovered_text_keyword_searchable()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Insert(repo, "scan@x", "scan.pdf", AttachmentTextExtractor.StatusNoText);
        var attId = repo.GetById(id)!.Attachments[0].Id;

        // Before OCR: the word isn't in any indexable column.
        FtsMatchCount(db, "quarterly").ShouldBe(0);

        repo.SaveOcrText(attId, id, "Quarterly revenue was 12345 dollars");

        // attachment_text rebuilt + FTS (via the update trigger) now matches.
        AttachmentTextCol(db, id)!.ShouldContain("Quarterly revenue");
        FtsMatchCount(db, "quarterly").ShouldBe(1);
        FtsMatchCount(db, "12345").ShouldBe(1);
    }

    [Fact]
    public void Upsert_noop_rescan_does_not_clobber_ocr_recovered_attachment_text()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        // Indexer sees a scanned PDF as no_text; the embedder's OCR pass then
        // recovers the text and rebuilds attachment_text so keyword search works.
        long id = Insert(repo, "scan@x", "scan.pdf", AttachmentTextExtractor.StatusNoText);
        var attId = repo.GetById(id)!.Attachments[0].Id;
        repo.SaveOcrText(attId, id, "Quarterly revenue was 12345 dollars");
        FtsMatchCount(db, "quarterly").ShouldBe(1);

        // A periodic rescan re-parses the .eml: a scanned PDF still extracts as
        // no_text (empty text) and the body content_hash is unchanged (same id →
        // same "h-{id}"). ReplaceAttachments is skipped, so the attachments row
        // keeps its OCR text — the message's attachment_text must NOT be wiped to
        // match the empty fresh parse. This is the regression that silently broke
        // keyword search for OCR'd docs after a cross-machine DB import.
        Insert(repo, "scan@x", "scan.pdf", AttachmentTextExtractor.StatusNoText);

        AttachmentTextCol(db, id)!.ShouldContain("Quarterly revenue");
        FtsMatchCount(db, "quarterly").ShouldBe(1);
        repo.GetById(id)!.Attachments[0].ExtractionStatus.ShouldBe(AttachmentTextExtractor.StatusOcr);
    }

    [Fact]
    public void Upsert_content_change_refreshes_attachment_text_from_the_new_parse()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        long id = Insert(repo, "doc@x", "old.pdf", AttachmentTextExtractor.StatusNoText);
        repo.SaveOcrText(repo.GetById(id)!.Attachments[0].Id, id, "obsolete recovered text");
        AttachmentTextCol(db, id)!.ShouldContain("obsolete");

        // The .eml genuinely changed (new content_hash) and the fresh parse
        // carries real extracted text. ReplaceAttachments runs, so attachment_text
        // must be rebuilt from the new parse — the preserve-on-no-op fix must not
        // freeze it forever.
        var changed = new ParsedMessage(
            MessageId: "doc@x", ThreadId: "doc@x", Subject: "s", FromAddress: "a@x", FromName: null,
            ToAddresses: [], CcAddresses: [], DateSent: DateTimeOffset.UtcNow, BodyText: "body",
            BodyHtml: null, RawHeaders: "Message-ID: <doc@x>\r\n", SizeBytes: 100, ContentHash: "h-doc@x-v2",
            Attachments: [new ParsedAttachment(0, "new.pdf", "application/pdf", 100,
                ExtractedText: "brand new invoice text", ExtractionStatus: AttachmentTextExtractor.StatusDone)]);
        repo.Upsert(changed, "INBOX", "INBOX/cur", "doc@x.eml", DateTimeOffset.UtcNow);

        var text = AttachmentTextCol(db, id)!;
        text.ShouldContain("brand new invoice");
        text.ShouldNotContain("obsolete");
    }

    [Fact]
    public void MarkAttachmentOcrFailed_sets_failed_so_it_is_not_re_selected()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Insert(repo, "bad@x", "corrupt.pdf", AttachmentTextExtractor.StatusNoText);
        var attId = repo.GetById(id)!.Attachments[0].Id;

        repo.MarkAttachmentOcrFailed(attId);

        repo.GetById(id)!.Attachments[0].ExtractionStatus.ShouldBe(AttachmentTextExtractor.StatusFailed);
        repo.EnumerateAttachmentsNeedingOcr(50).ShouldBeEmpty();
    }

    [Fact]
    public void SaveOcrText_is_a_noop_when_the_attachment_is_no_longer_a_candidate()
    {
        // Rowid-reuse guard: if the row was replaced/reprocessed since the OCR
        // pass selected it (now 'done'), SaveOcrText must not overwrite it or
        // re-queue the message off a stale assumption.
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Insert(repo, "done@x", "ok.pdf", AttachmentTextExtractor.StatusDone);
        var att = repo.GetById(id)!.Attachments[0];
        SetEmbeddedAt(db, id);

        repo.SaveOcrText(att.Id, id, "SHOULD NOT BE WRITTEN");

        var after = repo.GetById(id)!.Attachments[0];
        after.ExtractionStatus.ShouldBe(AttachmentTextExtractor.StatusDone);
        (after.ExtractedText ?? "").ShouldNotContain("SHOULD NOT");
        EmbeddedAt(db, id).ShouldNotBeNull(); // guard bailed → no re-queue
    }

    // ── Image OCR queue + inline-image backfill + split counts ───────────────

    [Fact]
    public void EnumerateImagesNeedingOcr_selects_only_unsupported_images_above_the_byte_gate()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        Insert(repo, "big@x", "photo.png", AttachmentTextExtractor.StatusUnsupported, contentType: "image/png", size: 60000);
        Insert(repo, "small@x", "tiny.png", AttachmentTextExtractor.StatusUnsupported, contentType: "image/png", size: 1000);   // below gate
        Insert(repo, "gif@x", "anim.gif", AttachmentTextExtractor.StatusUnsupported, contentType: "image/gif", size: 60000);    // gif excluded
        Insert(repo, "done@x", "ok.png", AttachmentTextExtractor.StatusDone, contentType: "image/png", size: 60000);            // wrong status
        Insert(repo, "pdf@x", "scan.pdf", AttachmentTextExtractor.StatusNoText, contentType: "application/pdf", size: 60000);   // not an image

        var pending = repo.EnumerateImagesNeedingOcr(50, minBytes: 50 * 1024);

        pending.Count.ShouldBe(1);
        pending[0].MessageIdHeader.ShouldBe("big@x");
    }

    [Fact]
    public void EnumerateImagesNeedingOcr_includes_images_mislabeled_as_octet_stream()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        // Real photos shipped with a generic content-type but an image filename.
        Insert(repo, "octet-png@x", "IMG_5677.png", AttachmentTextExtractor.StatusUnsupported, contentType: "application/octet-stream", size: 200000);
        Insert(repo, "octet-jpeg@x", "scan.jpeg", AttachmentTextExtractor.StatusUnsupported, contentType: "application/octet-stream", size: 200000);
        // Generic content-type but a non-image extension → not a candidate.
        Insert(repo, "octet-zip@x", "data.zip", AttachmentTextExtractor.StatusUnsupported, contentType: "application/octet-stream", size: 200000);
        // Generic content-type + image extension but below the byte gate.
        Insert(repo, "octet-tiny@x", "logo.png", AttachmentTextExtractor.StatusUnsupported, contentType: "application/octet-stream", size: 1000);
        // GIF stays excluded even when mislabeled.
        Insert(repo, "octet-gif@x", "anim.gif", AttachmentTextExtractor.StatusUnsupported, contentType: "application/octet-stream", size: 200000);

        var pending = repo.EnumerateImagesNeedingOcr(50, minBytes: 50 * 1024);

        pending.Select(p => p.MessageIdHeader).ShouldBe(["octet-png@x", "octet-jpeg@x"], ignoreOrder: true);
    }

    [Fact]
    public void EnumerateImagesNeedingOcr_excludes_soft_deleted()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Insert(repo, "del@x", "photo.png", AttachmentTextExtractor.StatusUnsupported, contentType: "image/png", size: 60000);
        repo.MarkDeleted([id], DateTimeOffset.UtcNow);

        repo.EnumerateImagesNeedingOcr(50, 50 * 1024).ShouldBeEmpty();
    }

    [Fact]
    public void MarkAttachmentImageNoText_moves_the_image_off_the_queue()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Insert(repo, "img@x", "photo.png", AttachmentTextExtractor.StatusUnsupported, contentType: "image/png", size: 60000);
        var attId = repo.GetById(id)!.Attachments[0].Id;

        repo.MarkAttachmentImageNoText(attId);

        repo.GetById(id)!.Attachments[0].ExtractionStatus.ShouldBe(AttachmentTextExtractor.StatusNoText);
        repo.EnumerateImagesNeedingOcr(50, 50 * 1024).ShouldBeEmpty();
    }

    [Fact]
    public void AddInlineAttachments_captures_an_inline_image_and_sets_has_attachments()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = InsertBare(repo, "inlineonly@x");   // the cid:-only shape: zero attachment rows
        HasAttachments(db, id).ShouldBeFalse();

        var inserted = repo.AddInlineAttachments(id,
            [new ParsedAttachment(0, "IMG_8576.jpg", "image/jpeg", 60000, ExtractedText: null, ExtractionStatus: AttachmentTextExtractor.StatusUnsupported)]);

        inserted.ShouldBe(1);
        HasAttachments(db, id).ShouldBeTrue();
        var att = repo.GetById(id)!.Attachments.ShouldHaveSingleItem();
        att.PartIndex.ShouldBe(0);
        att.ContentType.ShouldBe("image/jpeg");
        att.ExtractionStatus.ShouldBe(AttachmentTextExtractor.StatusUnsupported);
    }

    [Fact]
    public void AddInlineAttachments_appends_without_disturbing_existing_rows_and_is_idempotent()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Insert(repo, "mixed@x", "real.pdf", AttachmentTextExtractor.StatusDone);   // row at part_index 0
        ParsedAttachment Inline() => new(1, "inline.png", "image/png", 60000, null, AttachmentTextExtractor.StatusUnsupported);

        repo.AddInlineAttachments(id, [Inline()]).ShouldBe(1);

        var atts = repo.GetById(id)!.Attachments;
        atts.Count.ShouldBe(2);
        var existing = atts.Single(a => a.PartIndex == 0);
        existing.FileName.ShouldBe("real.pdf");                                  // untouched
        existing.ExtractionStatus.ShouldBe(AttachmentTextExtractor.StatusDone);

        repo.AddInlineAttachments(id, [Inline()]).ShouldBe(0);                   // INSERT OR IGNORE — no dup
        repo.GetById(id)!.Attachments.Count.ShouldBe(2);
    }

    [Fact]
    public void AddInlineAttachments_with_recoverable_text_requeues_and_indexes_it()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = InsertBare(repo, "textinline@x");
        SetEmbeddedAt(db, id);
        FtsMatchCount(db, "spreadsheet").ShouldBe(0);

        repo.AddInlineAttachments(id,
            [new ParsedAttachment(0, "sheet.png", "image/png", 500, ExtractedText: "quarterly spreadsheet totals", ExtractionStatus: AttachmentTextExtractor.StatusOcr)]);

        EmbeddedAt(db, id).ShouldBeNull();                                       // text added → re-queued
        AttachmentTextCol(db, id)!.ShouldContain("quarterly spreadsheet");
        FtsMatchCount(db, "spreadsheet").ShouldBe(1);
    }

    [Fact]
    public void GetAttachmentPartIndexes_returns_existing_indexes()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        long id = Insert(repo, "idx@x", "doc.pdf", AttachmentTextExtractor.StatusDone);   // index 0
        repo.AddInlineAttachments(id, [new ParsedAttachment(1, "a.png", "image/png", 60000, null, AttachmentTextExtractor.StatusUnsupported)]);

        repo.GetAttachmentPartIndexes(id).ShouldBe(new HashSet<int> { 0, 1 });
    }

    [Fact]
    public void OcrCounts_splits_pending_and_recovered_by_source()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        Insert(repo, "pdfq@x", "scan.pdf", AttachmentTextExtractor.StatusNoText, contentType: "application/pdf", size: 60000);   // PdfPending
        Insert(repo, "imgq@x", "photo.png", AttachmentTextExtractor.StatusUnsupported, contentType: "image/png", size: 60000);  // ImagePending
        Insert(repo, "small@x", "tiny.png", AttachmentTextExtractor.StatusUnsupported, contentType: "image/png", size: 1000);   // below gate — uncounted
        Insert(repo, "pdfrec@x", "d.pdf", AttachmentTextExtractor.StatusOcr, contentType: "application/pdf", size: 60000);       // PdfRecovered
        Insert(repo, "imgrec@x", "r.png", AttachmentTextExtractor.StatusOcr, contentType: "image/png", size: 60000);            // ImageRecovered

        var c = repo.OcrCounts(imageMinBytes: 50 * 1024);

        c.PdfPending.ShouldBe(1);
        c.ImagePending.ShouldBe(1);
        c.PdfRecovered.ShouldBe(1);
        c.ImageRecovered.ShouldBe(1);
        c.Pending.ShouldBe(2);
        c.Recovered.ShouldBe(2);
    }

    private static bool HasAttachments(TempDatabase db, long messageId)
    {
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT has_attachments FROM messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", messageId);
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static void SetEmbeddedAt(TempDatabase db, long messageId)
    {
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE messages SET embedded_at = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.ExecuteNonQuery();
    }

    private static string? EmbeddedAt(TempDatabase db, long messageId)
    {
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT embedded_at FROM messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", messageId);
        return cmd.ExecuteScalar() as string;
    }

    private static string? AttachmentTextCol(TempDatabase db, long messageId)
    {
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT attachment_text FROM messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", messageId);
        return cmd.ExecuteScalar() as string;
    }

    private static int FtsMatchCount(TempDatabase db, string term)
    {
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH $q";
        cmd.Parameters.AddWithValue("$q", term);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
