using Mailvec.Core.Data;
using Mailvec.Core.Models;
using Mailvec.Core.Parsing;

namespace Mailvec.Core.Tests.Data;

public class MessageRepositoryTests
{
    private static ParsedMessage Sample(string id = "test-001@example.com", string? subject = "Hi") =>
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
            HasAttachments: false);

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
}
