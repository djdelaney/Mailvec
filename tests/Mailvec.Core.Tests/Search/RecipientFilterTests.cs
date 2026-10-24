using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Models;
using Mailvec.Core.Parsing;
using Mailvec.Core.Search;
using Mailvec.Core.Tests.Data;

namespace Mailvec.Core.Tests.Search;

/// <summary>
/// <see cref="SearchFilters.ToContains"/>: a To/Cc recipient substring, across
/// the keyword, vector and browse paths (which share <c>SearchFilterSql</c>).
///
/// <para>The case that matters most is the plus-address. Recipients are stored
/// as System.Text.Json output with the default encoder, which writes <c>+</c>
/// as <c>+</c> — so a LIKE on the raw column misses every
/// <c>dan+alert@…</c> message, the exact mail the filter exists to find. The
/// clause decodes through SQLite's JSON functions instead.</para>
/// </summary>
public class RecipientFilterTests
{
    private static ParsedMessage M(string id, IReadOnlyList<EmailAddress>? to = null, IReadOnlyList<EmailAddress>? cc = null) => new(
        MessageId: id,
        ThreadId: id,
        Subject: "subj",
        FromAddress: "monitor@example.com",
        FromName: null,
        ToAddresses: to ?? [],
        CcAddresses: cc ?? [],
        DateSent: DateTimeOffset.UtcNow,
        BodyText: "ramen body text",
        BodyHtml: null,
        RawHeaders: $"Message-ID: <{id}>\r\n",
        SizeBytes: 100,
        ContentHash: $"hash-{id}",
        Attachments: []);

    private static float[] OneHot(int hotIndex, int dim = 1024)
    {
        var v = new float[dim];
        v[hotIndex] = 1f;
        return v;
    }

    private static void Seed(TempDatabase db)
    {
        var repo = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;
        void Add(ParsedMessage m)
        {
            long id = repo.Upsert(m, "INBOX", "INBOX/cur", m.MessageId, now);
            chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "x", 1)], [OneHot(0)], now);
        }
        Add(M("alert@x", to: [new EmailAddress(null, "Dan+Alert@Hactar.com")]));
        Add(M("cc-alert@x", to: [new EmailAddress(null, "someone@else.com")], cc: [new EmailAddress(null, "dan+alert@hactar.com")]));
        Add(M("plain@x", to: [new EmailAddress("Dan Delaney", "dan@hactar.com")]));
        Add(M("named@x", to: [new EmailAddress("Kathleen O'Marra", "kat@example.com")]));
        Add(M("underscore@x", to: [new EmailAddress(null, "a_b@example.com")]));
        Add(M("axb@x", to: [new EmailAddress(null, "axb@example.com")]));
        Add(M("nobody@x"));
    }

    public static TheoryData<string, string[]> Cases() => new()
    {
        // The plus-address, stored as dan+alert — and case-insensitive.
        { "dan+alert@hactar.com", ["alert@x", "cc-alert@x"] },
        { "DAN+ALERT", ["alert@x", "cc-alert@x"] },
        // A substring of the base address matches the plus-addressed ones too.
        { "dan", ["alert@x", "cc-alert@x", "plain@x"] },
        // Display name, with an apostrophe the encoder stores as '.
        { "o'marra", ["named@x"] },
        // `_` is literal, not LIKE's single-character wildcard.
        { "a_b@", ["underscore@x"] },
        { "nothing-matches", [] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Keyword_vector_and_browse_agree_on_the_recipient_filter(string value, string[] expected)
    {
        using var db = new TempDatabase();
        Seed(db);
        var filters = new SearchFilters(ToContains: value);

        new KeywordSearchService(db.Connections).Search("ramen", filters: filters)
            .Select(h => h.MessageIdHeader).ShouldBe(expected, ignoreOrder: true);
        new VectorSearchService(db.Connections, embeddings: null!)
            .SearchByVector(OneHot(0), limit: 20, k: 100, filters: filters)
            .Select(h => h.MessageIdHeader).ShouldBe(expected, ignoreOrder: true);
        new MessageRepository(db.Connections).BrowseByFilters(filters, limit: 20)
            .Select(m => m.MessageId).ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public void The_plus_address_really_is_stored_escaped()
    {
        // The premise of the JSON-decoding clause. If storage ever stops
        // escaping `+`, the clause is still correct — but this documents why a
        // raw LIKE was never an option.
        using var db = new TempDatabase();
        Seed(db);
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT to_addresses FROM messages WHERE message_id = 'alert@x'";
        (cmd.ExecuteScalar() as string).ShouldNotBeNull().ShouldContain("\\u002B", Case.Sensitive);
    }

    [Fact]
    public void A_row_with_malformed_recipient_json_does_not_break_the_query()
    {
        using var db = new TempDatabase();
        Seed(db);
        using (var conn = db.Connections.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE messages SET to_addresses = 'not json', cc_addresses = NULL WHERE message_id = 'nobody@x'";
            cmd.ExecuteNonQuery();
        }

        new MessageRepository(db.Connections).BrowseByFilters(new SearchFilters(ToContains: "dan+alert"), limit: 20)
            .Select(m => m.MessageId).ShouldBe(["alert@x", "cc-alert@x"], ignoreOrder: true);
    }

    [Fact]
    public void Combines_with_other_filters_and_counts_as_a_filter()
    {
        using var db = new TempDatabase();
        Seed(db);

        new MessageRepository(db.Connections)
            .BrowseByFilters(new SearchFilters(ToContains: "dan+alert", Folder: "Elsewhere"), limit: 20)
            .ShouldBeEmpty();
        new SearchFilters(ToContains: "x").IsEmpty.ShouldBeFalse();
    }
}
