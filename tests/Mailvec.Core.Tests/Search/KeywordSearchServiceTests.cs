using Mailvec.Core.Data;
using Mailvec.Core.Models;
using Mailvec.Core.Parsing;
using Mailvec.Core.Search;
using Mailvec.Core.Tests.Data;

namespace Mailvec.Core.Tests.Search;

public class KeywordSearchServiceTests
{
    private static ParsedMessage M(string id, string subject, string body, string? from = "alice@example.com") => new(
        MessageId: id,
        ThreadId: id,
        Subject: subject,
        FromAddress: from,
        FromName: null,
        ToAddresses: [],
        CcAddresses: [],
        DateSent: DateTimeOffset.UtcNow,
        BodyText: body,
        BodyHtml: null,
        RawHeaders: $"Message-ID: <{id}>\r\n",
        SizeBytes: 100,
        HasAttachments: false);

    [Fact]
    public void Returns_messages_matching_subject_or_body()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var search = new KeywordSearchService(db.Connections);
        var now = DateTimeOffset.UtcNow;

        repo.Upsert(M("a@x", "Lunch on Friday", "ramen at 12:30"),       "INBOX", "INBOX/cur", "a", now);
        repo.Upsert(M("b@x", "Quarterly report", "sales numbers attached"), "INBOX", "INBOX/cur", "b", now);
        repo.Upsert(M("c@x", "Hello",            "ramen sounds great"),  "INBOX", "INBOX/cur", "c", now);

        var hits = search.Search("ramen");
        hits.Count.ShouldBe(2);
        hits.Select(h => h.MessageIdHeader).ShouldBe(new[] { "a@x", "c@x" }, ignoreOrder: true);
    }

    [Fact]
    public void Excludes_soft_deleted_messages()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var search = new KeywordSearchService(db.Connections);
        var now = DateTimeOffset.UtcNow;

        var keepId = repo.Upsert(M("keep@x", "ramen recipe",  "tonkotsu broth"), "INBOX", "INBOX/cur", "k", now);
        var dropId = repo.Upsert(M("drop@x", "ramen takeout", "miso ramen menu"), "INBOX", "INBOX/cur", "d", now);

        repo.MarkDeleted([dropId], DateTimeOffset.UtcNow);

        var hits = search.Search("ramen");
        hits.Single().MessageIdHeader.ShouldBe("keep@x");
    }

    [Fact]
    public void Snippet_brackets_the_matched_term()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var search = new KeywordSearchService(db.Connections);

        repo.Upsert(M("q@x", "Hi", "the new ramen place opens Friday"),
            "INBOX", "INBOX/cur", "q", DateTimeOffset.UtcNow);

        var hit = search.Search("ramen").Single();
        hit.Snippet.ShouldContain("[ramen]");
    }

    [Fact]
    public void Boolean_operators_work()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var search = new KeywordSearchService(db.Connections);
        var now = DateTimeOffset.UtcNow;

        repo.Upsert(M("a@x", "lunch friday", "ramen"),         "INBOX", "INBOX/cur", "a", now);
        repo.Upsert(M("b@x", "lunch saturday", "sushi"),       "INBOX", "INBOX/cur", "b", now);
        repo.Upsert(M("c@x", "dinner friday", "pizza"),        "INBOX", "INBOX/cur", "c", now);

        var hits = search.Search("lunch AND friday");
        hits.Single().MessageIdHeader.ShouldBe("a@x");
    }
}
