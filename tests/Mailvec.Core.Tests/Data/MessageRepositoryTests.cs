using Mailvec.Core.Data;
using Mailvec.Core.Models;
using Mailvec.Core.Parsing;

namespace Mailvec.Core.Tests.Data;

public class MessageRepositoryTests
{
    private static ParsedMessage Sample(string id = "test-001@example.com", string? subject = "Hi", IReadOnlyList<ParsedAttachment>? attachments = null) =>
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
            Attachments: attachments ?? []);

    [Fact]
    public void Inserts_then_reads_back_a_message()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var id = repo.Upsert(Sample(), folder: "INBOX", "INBOX/cur", "1736780100.1.host:2,S", DateTimeOffset.UtcNow);
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

        var id1 = repo.Upsert(Sample(subject: "Original"), "INBOX", "INBOX/new", "1736780100.1.host", now);
        var id2 = repo.Upsert(Sample(subject: "Edited"), "INBOX", "INBOX/cur", "1736780100.1.host:2,S", now);

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
    public void Re_upsert_replaces_attachments_wholesale()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var initial = new List<ParsedAttachment>
        {
            new(0, "old-a.pdf", "application/pdf", 100),
            new(1, "old-b.pdf", "application/pdf", 200),
        };
        var id = repo.Upsert(Sample(attachments: initial), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);

        var replacement = new List<ParsedAttachment>
        {
            new(0, "new-only.pdf", "application/pdf", 300),
        };
        repo.Upsert(Sample(attachments: replacement), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);

        var msg = repo.GetById(id).ShouldNotBeNull();
        msg.Attachments.Count.ShouldBe(1);
        msg.Attachments[0].FileName.ShouldBe("new-only.pdf");
    }

    [Fact]
    public void Re_upsert_to_no_attachments_clears_them()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);

        var initial = new List<ParsedAttachment>
        {
            new(0, "doc.pdf", "application/pdf", 100),
        };
        var id = repo.Upsert(Sample(attachments: initial), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);
        repo.GetById(id).ShouldNotBeNull().HasAttachments.ShouldBeTrue();

        repo.Upsert(Sample(attachments: []), "INBOX", "INBOX/cur", "f1", DateTimeOffset.UtcNow);

        var msg = repo.GetById(id).ShouldNotBeNull();
        msg.Attachments.ShouldBeEmpty();
        msg.HasAttachments.ShouldBeFalse();
    }
}
