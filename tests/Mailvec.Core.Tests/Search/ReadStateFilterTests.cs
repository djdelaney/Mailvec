using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Parsing;
using Mailvec.Core.Search;
using Mailvec.Core.Tests.Data;

namespace Mailvec.Core.Tests.Search;

/// <summary>
/// The <see cref="SearchFilters.Unread"/> filter and
/// <see cref="MessageRepository.GetReadStates"/> answer the same question —
/// "unseen anywhere is unread; read needs a tracked copy" — and must agree,
/// across the keyword, vector and browse paths.
/// </summary>
public class ReadStateFilterTests
{
    private static ParsedMessage M(string id) => new(
        MessageId: id,
        ThreadId: id,
        Subject: "subj",
        FromAddress: "alice@example.com",
        FromName: null,
        ToAddresses: [],
        CcAddresses: [],
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

    /// <summary>
    /// read@x: one seen copy. unread@x: one unseen copy. mixed@x: seen in
    /// INBOX, unseen in Alerts. untracked@x: no sync_state row at all.
    /// </summary>
    private static Dictionary<string, long> Seed(TempDatabase db)
    {
        var repo = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var syncState = new SyncStateRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;
        var ids = new Dictionary<string, long>();
        foreach (var mid in new[] { "read@x", "unread@x", "mixed@x", "untracked@x" })
        {
            ids[mid] = repo.Upsert(M(mid), "INBOX", "INBOX/cur", mid, now);
            chunks.ReplaceChunksForMessage(ids[mid], [new TextChunk(0, "x", 1)], [OneHot(0)], now);
        }

        using var conn = db.Connections.Open();
        using var tx = conn.BeginTransaction();
        void Copy(string path, string mid, string folder) =>
            syncState.Upsert(conn, tx, path, mid, now, "h", folder, null, null);
        Copy("/mail/INBOX/cur/r.host,U=1:2,S", "read@x", "INBOX");
        Copy("/mail/INBOX/cur/u.host,U=2:2,", "unread@x", "INBOX");
        Copy("/mail/INBOX/cur/m.host,U=3:2,S", "mixed@x", "INBOX");
        Copy("/mail/Alerts/new/m.host,U=9", "mixed@x", "Alerts");
        tx.Commit();
        return ids;
    }

    [Theory]
    [InlineData(true, new[] { "mixed@x", "unread@x" })]
    [InlineData(false, new[] { "read@x" })]
    public void Keyword_vector_and_browse_agree_on_the_unread_filter(bool unread, string[] expected)
    {
        using var db = new TempDatabase();
        Seed(db);
        var filters = new SearchFilters(Unread: unread);

        new KeywordSearchService(db.Connections).Search("ramen", filters: filters)
            .Select(h => h.MessageIdHeader).ShouldBe(expected, ignoreOrder: true);
        new VectorSearchService(db.Connections, embeddings: null!)
            .SearchByVector(OneHot(0), limit: 10, k: 100, filters: filters)
            .Select(h => h.MessageIdHeader).ShouldBe(expected, ignoreOrder: true);
        new MessageRepository(db.Connections).BrowseByFilters(filters, limit: 10)
            .Select(m => m.MessageId).ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public void A_null_unread_filter_does_not_filter()
    {
        using var db = new TempDatabase();
        Seed(db);
        new MessageRepository(db.Connections).BrowseByFilters(SearchFilters.None, limit: 10).Count.ShouldBe(4);
        new SearchFilters(Unread: false).IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void GetReadStates_matches_the_filter_and_omits_untracked_messages()
    {
        using var db = new TempDatabase();
        Seed(db);

        var states = new MessageRepository(db.Connections)
            .GetReadStates(["read@x", "unread@x", "mixed@x", "untracked@x", "nonexistent@x"]);

        states["read@x"].ShouldBeTrue();
        states["unread@x"].ShouldBeFalse();
        states["mixed@x"].ShouldBeFalse(); // unseen in Alerts → unread
        // Unknown is absent, never a guessed "read".
        states.ContainsKey("untracked@x").ShouldBeFalse();
        states.ContainsKey("nonexistent@x").ShouldBeFalse();
    }

    [Fact]
    public void GetReadStates_handles_more_ids_than_one_statement_can_bind()
    {
        using var db = new TempDatabase();
        Seed(db);
        var ids = Enumerable.Range(0, 1200).Select(i => $"pad{i}@x").Append("unread@x").ToList();

        var states = new MessageRepository(db.Connections).GetReadStates(ids);

        states.Count.ShouldBe(1);
        states["unread@x"].ShouldBeFalse();
    }
}
