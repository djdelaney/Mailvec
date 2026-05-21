using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Models;
using Mailvec.Core.Parsing;

namespace Mailvec.Core.Tests.Data;

public class MessageRepositoryTests
{
    private static ParsedMessage Sample(string id = "test-001@example.com", string? subject = "Hi", IReadOnlyList<ParsedAttachment>? attachments = null, string? contentHash = null) =>
        new(
            MessageId: id,
            ThreadId: id,
            Subject: subject,
            FromAddress: "alice@example.com",
            FromName: "Alice",
            ToAddresses: [new EmailAddress("Bob", "bob@example.com")],
            CcAddresses: [],
            DateSent: new DateTimeOffset(2025, 1, 13, 10, 15, 0, TimeSpan.FromHours(-5)),
            BodyText: "lunch on friday at the ramen place",
            BodyHtml: null,
            RawHeaders: "Message-ID: <test-001@example.com>\r\n",
            SizeBytes: 512,
            ContentHash: contentHash ?? $"hash-{id}",
            Attachments: attachments ?? []);

    [Fact]
    public void Inserts_then_reads_back_a_message()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        long id = repo.Upsert(Sample(), folder: "INBOX", "INBOX/cur", "1736780100.1.host:2,S", DateTimeOffset.UtcNow);
        id.ShouldBeGreaterThan(0);

        var msg = repo.GetById(id).ShouldNotBeNull();
        msg.MessageId.ShouldBe("test-001@example.com");
        msg.Subject.ShouldBe("Hi");
        msg.FromName.ShouldBe("Alice");
        msg.ToAddresses.Single().Address.ShouldBe("bob@example.com");
        msg.HasAttachments.ShouldBeFalse();
        msg.DeletedAt.ShouldBeNull();
    }

    [Fact]
    public void Upsert_is_idempotent_and_updates_in_place_on_duplicate_message_id()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        long id1 = repo.Upsert(Sample(subject: "Original"), "INBOX", "INBOX/new", "1736780100.1.host", now);
        long id2 = repo.Upsert(Sample(subject: "Edited"), "INBOX", "INBOX/cur", "1736780100.1.host:2,S", now);

        id2.ShouldBe(id1);
        repo.CountAll().ShouldBe(1);
        repo.GetById(id1).ShouldNotBeNull().Subject.ShouldBe("Edited");
    }

    [Fact]
    public void MarkDeleted_sets_deleted_at_and_drops_active_count()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var id1 = repo.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);
        var id2 = repo.Upsert(Sample("b@x"), "INBOX", "INBOX/cur", "f2", DateTimeOffset.UtcNow);

        repo.CountAll().ShouldBe(2);
        var affected = repo.MarkDeleted([id1], DateTimeOffset.UtcNow);

        affected.ShouldBe(1);
        repo.CountAll().ShouldBe(1);
        repo.GetById(id1).ShouldNotBeNull().DeletedAt.ShouldNotBeNull();
        repo.GetById(id2).ShouldNotBeNull().DeletedAt.ShouldBeNull();
    }

    [Fact]
    public void Re_upserting_a_soft_deleted_message_id_clears_deleted_at()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var id = repo.Upsert(Sample(), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);
        repo.MarkDeleted([id], DateTimeOffset.UtcNow);
        repo.GetById(id).ShouldNotBeNull().DeletedAt.ShouldNotBeNull();

        repo.Upsert(Sample(), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);
        repo.GetById(id).ShouldNotBeNull().DeletedAt.ShouldBeNull();
    }

    [Fact]
    public void GetArchiveStats_returns_count_and_date_range_excluding_soft_deleted()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var oldest = new DateTimeOffset(2014, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var middle = new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var newest = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

        var p1 = Sample("a@x") with { DateSent = oldest };
        var p2 = Sample("b@x") with { DateSent = middle };
        var p3 = Sample("c@x") with { DateSent = newest };
        repo.Upsert(p1, "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);
        var midId = repo.Upsert(p2, "INBOX", "INBOX/cur", "f2", DateTimeOffset.UtcNow);
        repo.Upsert(p3, "INBOX", "INBOX/cur", "f3", DateTimeOffset.UtcNow);

        var stats = repo.GetArchiveStats();
        stats.TotalMessages.ShouldBe(3);
        stats.OldestDate.ShouldBe(oldest);
        stats.LatestDate.ShouldBe(newest);

        // Soft-deleting the only newest message should pull LatestDate back.
        repo.Upsert(Sample("c@x") with { DateSent = newest }, "INBOX", "INBOX/cur", "f3", DateTimeOffset.UtcNow);
        var newestRow = repo.GetByMessageId("c@x").ShouldNotBeNull();
        repo.MarkDeleted([newestRow.Id], DateTimeOffset.UtcNow);

        var afterDelete = repo.GetArchiveStats();
        afterDelete.TotalMessages.ShouldBe(2);
        afterDelete.LatestDate.ShouldBe(middle);
    }

    [Fact]
    public void GetArchiveStats_on_empty_archive_returns_zero_and_nulls()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var stats = repo.GetArchiveStats();
        stats.TotalMessages.ShouldBe(0);
        stats.OldestDate.ShouldBeNull();
        stats.LatestDate.ShouldBeNull();
    }

    [Fact]
    public void Upsert_persists_attachments_and_GetById_hydrates_them()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var attachments = new List<ParsedAttachment>
        {
            new(PartIndex: 0, FileName: "mortgage_statement_2024.pdf", ContentType: "application/pdf", SizeBytes: 12345),
            new(PartIndex: 1, FileName: "ledger.xlsx", ContentType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", SizeBytes: 6789),
        };

        var id = repo.Upsert(Sample(attachments: attachments), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);

        var msg = repo.GetById(id).ShouldNotBeNull();
        msg.HasAttachments.ShouldBeTrue();
        msg.Attachments.Count.ShouldBe(2);
        msg.Attachments[0].FileName.ShouldBe("mortgage_statement_2024.pdf");
        msg.Attachments[0].ContentType.ShouldBe("application/pdf");
        msg.Attachments[0].SizeBytes.ShouldBe(12345);
        msg.Attachments[1].FileName.ShouldBe("ledger.xlsx");
    }

    [Fact]
    public void Re_upsert_replaces_attachments_wholesale_when_content_changed()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var initial = new List<ParsedAttachment>
        {
            new(0, "old-a.pdf", "application/pdf", 100),
            new(1, "old-b.pdf", "application/pdf", 200),
        };
        long id = repo.Upsert(Sample(attachments: initial, contentHash: "h1"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);

        var replacement = new List<ParsedAttachment>
        {
            new(0, "new-only.pdf", "application/pdf", 300),
        };
        // Upsert with a different content_hash to model "the message body
        // mutated upstream", which is the only realistic way an attachment
        // list can change in production (parser hashes the body, body
        // includes attachment parts, so attachment delta => hash delta).
        repo.Upsert(Sample(attachments: replacement, contentHash: "h2"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);

        var msg = repo.GetById(id).ShouldNotBeNull();
        msg.Attachments.Count.ShouldBe(1);
        msg.Attachments[0].FileName.ShouldBe("new-only.pdf");
    }

    [Fact]
    public void Re_upsert_with_unchanged_hash_preserves_attachments()
    {
        // The complement of the above: when content_hash matches the prior
        // row, ReplaceAttachments is intentionally skipped so extracted_text
        // (populated by the indexer's AttachmentTextExtractor pass) survives
        // mtime-only rescans. Even though the parsed attachment list here
        // differs, the row must keep the original attachments.
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var initial = new List<ParsedAttachment>
        {
            new(0, "kept.pdf", "application/pdf", 100),
        };
        long id = repo.Upsert(Sample(attachments: initial, contentHash: "stable"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);

        repo.Upsert(Sample(attachments: [], contentHash: "stable"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);

        var msg = repo.GetById(id).ShouldNotBeNull();
        msg.Attachments.Count.ShouldBe(1);
        msg.Attachments[0].FileName.ShouldBe("kept.pdf");
    }

    [Fact]
    public void Re_upsert_to_no_attachments_clears_them_when_content_changed()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var initial = new List<ParsedAttachment>
        {
            new(0, "doc.pdf", "application/pdf", 100),
        };
        long id = repo.Upsert(Sample(attachments: initial, contentHash: "h1"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);
        repo.GetById(id).ShouldNotBeNull().HasAttachments.ShouldBeTrue();

        repo.Upsert(Sample(attachments: [], contentHash: "h2"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);

        var msg = repo.GetById(id).ShouldNotBeNull();
        msg.Attachments.ShouldBeEmpty();
        msg.HasAttachments.ShouldBeFalse();
    }

    [Fact]
    public void Re_upsert_with_attachment_chunks_present_does_not_orphan_chunk_embeddings()
    {
        // Regression: ReplaceAttachments runs `DELETE FROM attachments` on
        // content-change, and `chunks.attachment_id REFERENCES attachments(id)
        // ON DELETE CASCADE` then silently deletes the attachment-sourced
        // chunks rows. chunk_embeddings is a vec0 virtual table and does NOT
        // participate in FK cascade — without an explicit pre-cascade DELETE
        // in ReplaceAttachments, the vec0 rows for those chunk_ids leak as
        // orphans and eventually collide with future MAX(id)+1 inserts, which
        // breaks the embedder with `UNIQUE constraint failed on
        // chunk_embeddings primary key`.
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        var initial = new List<ParsedAttachment>
        {
            new(0, "doc.pdf", "application/pdf", 100),
        };
        long id = repo.Upsert(Sample(attachments: initial, contentHash: "h1"), "INBOX", "INBOX/cur", "f1", now);
        var attId = repo.GetById(id).ShouldNotBeNull().Attachments[0].Id;

        // Embed one body chunk + one attachment-sourced chunk against this message.
        chunks.ReplaceChunksForMessage(
            id,
            [
                new TextChunk(0, "body text", 1),
                new TextChunk(1, "doc.pdf\n\nextracted attachment text", 1, Source: "attachment", AttachmentId: attId),
            ],
            [Hot(0), Hot(1)],
            now);

        chunks.CountForMessage(id).ShouldBe(2);
        chunks.CountOrphanEmbeddings().ShouldBe(0);

        // Content-change re-upsert: triggers ReplaceAttachments, whose
        // attachments DELETE cascades to the attachment chunk row. The vec0
        // row for that chunk must be cleared in the same transaction.
        var replacement = new List<ParsedAttachment>
        {
            new(0, "new.pdf", "application/pdf", 200),
        };
        repo.Upsert(Sample(attachments: replacement, contentHash: "h2"), "INBOX", "INBOX/cur", "f1", now);

        chunks.CountOrphanEmbeddings().ShouldBe(0);
    }

    private static float[] Hot(int index, int dim = 1024)
    {
        var v = new float[dim];
        v[index] = 1f;
        return v;
    }

    [Fact]
    public void Upsert_reports_ContentChanged_false_on_fresh_insert()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var outcome = repo.Upsert(Sample(contentHash: "h1"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);
        outcome.ContentChanged.ShouldBeFalse();
        outcome.Id.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Upsert_reports_ContentChanged_false_when_hash_unchanged()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        repo.Upsert(Sample(contentHash: "stable-hash"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);
        var outcome = repo.Upsert(Sample(contentHash: "stable-hash"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);

        outcome.ContentChanged.ShouldBeFalse();
    }

    [Fact]
    public void Upsert_reports_ContentChanged_true_when_hash_changed()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var first = repo.Upsert(Sample(contentHash: "h1"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);
        var second = repo.Upsert(Sample(contentHash: "h2-different"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);

        second.Id.ShouldBe(first.Id);
        second.ContentChanged.ShouldBeTrue();
    }

    [Fact]
    public void Upsert_persists_content_hash_for_subsequent_change_detection()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        repo.Upsert(Sample(contentHash: "h1"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);
        repo.Upsert(Sample(contentHash: "h2"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);
        // Third upsert with the same hash as the second should report no change.
        var outcome = repo.Upsert(Sample(contentHash: "h2"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);

        outcome.ContentChanged.ShouldBeFalse();
    }

    [Fact]
    public void CountSoftDeleted_only_counts_rows_with_deleted_at_set()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var keep = repo.Upsert(Sample("keep@x"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);
        var goneA = repo.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "f2", DateTimeOffset.UtcNow);
        var goneB = repo.Upsert(Sample("b@x"), "INBOX", "INBOX/cur", "f3", DateTimeOffset.UtcNow);

        repo.CountSoftDeleted().ShouldBe(0);

        repo.MarkDeleted([goneA, goneB], DateTimeOffset.UtcNow);

        repo.CountSoftDeleted().ShouldBe(2);
        repo.CountAll().ShouldBe(1);  // CountAll excludes soft-deleted
        _ = keep;
    }

    [Fact]
    public void PurgeSoftDeleted_removes_only_soft_deleted_rows_and_returns_count()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var keepId = repo.Upsert(Sample("keep@x"), "INBOX", "INBOX/cur", "fk", DateTimeOffset.UtcNow);
        var doomedId = repo.Upsert(Sample("doomed@x"), "INBOX", "INBOX/cur", "fd", DateTimeOffset.UtcNow);
        repo.MarkDeleted([doomedId], DateTimeOffset.UtcNow);

        var purged = repo.PurgeSoftDeleted();

        purged.ShouldBe(1);
        repo.GetById(keepId).ShouldNotBeNull();
        repo.GetByMessageId("doomed@x").ShouldBeNull();
        repo.CountSoftDeleted().ShouldBe(0);
    }

    [Fact]
    public void PurgeSoftDeleted_is_a_noop_when_nothing_is_soft_deleted()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        repo.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);
        repo.Upsert(Sample("b@x"), "INBOX", "INBOX/cur", "f2", DateTimeOffset.UtcNow);

        repo.PurgeSoftDeleted().ShouldBe(0);
        repo.CountAll().ShouldBe(2);
    }

    [Fact]
    public void PurgeSoftDeleted_cascades_to_attachments()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var attachments = new List<ParsedAttachment>
        {
            new(0, "doomed.pdf", "application/pdf", 100),
        };
        long doomedId = repo.Upsert(Sample("doomed@x", attachments: attachments), "INBOX", "INBOX/cur", "fd", DateTimeOffset.UtcNow);
        repo.MarkDeleted([doomedId], DateTimeOffset.UtcNow);

        repo.PurgeSoftDeleted().ShouldBe(1);

        // Attachments are FK-cascaded; nothing should reference the gone message.
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM attachments WHERE message_id = $id";
        cmd.Parameters.AddWithValue("$id", doomedId);
        Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(0);
    }

    [Fact]
    public void PurgeSoftDeleted_clears_FTS_entries_for_removed_messages()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var keepId = repo.Upsert(Sample("keep@x", subject: "alpha keepable subject"), "INBOX", "INBOX/cur", "fk", DateTimeOffset.UtcNow);
        var doomedId = repo.Upsert(Sample("doomed@x", subject: "alpha doomed subject"), "INBOX", "INBOX/cur", "fd", DateTimeOffset.UtcNow);
        repo.MarkDeleted([doomedId], DateTimeOffset.UtcNow);

        FtsRowidsForTerm(db, "alpha").ShouldBe(new long[] { keepId, doomedId }, ignoreOrder: true);

        repo.PurgeSoftDeleted().ShouldBe(1);

        FtsRowidsForTerm(db, "alpha").ShouldBe(new long[] { keepId });
    }

    private static long[] FtsRowidsForTerm(TempDatabase db, string term)
    {
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT rowid FROM messages_fts WHERE messages_fts MATCH $q";
        cmd.Parameters.AddWithValue("$q", term);
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids.ToArray();
    }
}
