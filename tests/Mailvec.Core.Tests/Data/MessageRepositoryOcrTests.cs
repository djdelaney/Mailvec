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
    private static long Insert(MessageRepository repo, string id, string fileName, string? status, string folder = "INBOX")
    {
        var parsed = new ParsedMessage(
            MessageId: id, ThreadId: id, Subject: "s", FromAddress: "a@x", FromName: null,
            ToAddresses: [], CcAddresses: [], DateSent: DateTimeOffset.UtcNow, BodyText: "body",
            BodyHtml: null, RawHeaders: $"Message-ID: <{id}>\r\n", SizeBytes: 100, ContentHash: $"h-{id}",
            Attachments: [new ParsedAttachment(0, fileName, "application/pdf", 100, ExtractedText: null, ExtractionStatus: status)]);
        return repo.Upsert(parsed, folder, $"{folder}/cur", id + ".eml", DateTimeOffset.UtcNow);
    }

    [Fact]
    public void EnumerateAttachmentsNeedingOcr_returns_only_no_text_pdfs()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        Insert(repo, "scan@x", "scan.pdf", AttachmentTextExtractor.StatusNoText);
        Insert(repo, "done@x", "ok.pdf", AttachmentTextExtractor.StatusDone);          // already extracted
        Insert(repo, "img@x", "photo.png", AttachmentTextExtractor.StatusNoText);      // not a PDF

        var pending = repo.EnumerateAttachmentsNeedingOcr(50);

        pending.Count.ShouldBe(1);
        pending[0].MessageIdHeader.ShouldBe("scan@x");
        pending[0].MaildirFilename.ShouldBe("scan@x.eml");
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
