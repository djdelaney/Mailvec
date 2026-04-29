using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Models;
using Mailvec.Core.Parsing;
using Mailvec.Core.Search;
using Mailvec.Core.Tests.Data;

namespace Mailvec.Core.Tests.Search;

public class VectorSearchServiceTests
{
    private static ParsedMessage M(string id, string subject, string body) => new(
        MessageId: id,
        ThreadId: id,
        Subject: subject,
        FromAddress: "alice@example.com",
        FromName: null,
        ToAddresses: [],
        CcAddresses: [],
        DateSent: DateTimeOffset.UtcNow,
        BodyText: body,
        BodyHtml: null,
        RawHeaders: $"Message-ID: <{id}>\r\n",
        SizeBytes: 100,
        Attachments: []);

    /// <summary>
    /// Build a 1024-dim "synthetic" embedding where one dimension is hot. Lets us
    /// reason about cosine/L2 distances without invoking a real model: vectors
    /// hot on the same dim are near each other, hot on different dims are far.
    /// </summary>
    private static float[] OneHot(int dim, int hotIndex = 0, float magnitude = 1f)
    {
        var v = new float[dim];
        v[hotIndex] = magnitude;
        return v;
    }

    [Fact]
    public void Returns_nearest_neighbours_ordered_by_distance()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var search = new VectorSearchService(db.Connections, ollama: null!);   // unused — we use SearchByVector

        var now = DateTimeOffset.UtcNow;
        var idA = messages.Upsert(M("a@x", "Alpha topic", "alpha body text"), "INBOX", "INBOX/cur", "a", now);
        var idB = messages.Upsert(M("b@x", "Beta topic",  "beta body text"),  "INBOX", "INBOX/cur", "b", now);
        var idC = messages.Upsert(M("c@x", "Gamma topic", "gamma body text"), "INBOX", "INBOX/cur", "c", now);

        // Each message gets one synthetic chunk with a one-hot vector at a distinct index.
        chunks.ReplaceChunksForMessage(idA, [new TextChunk(0, "alpha", 1)], [OneHot(1024, hotIndex: 0)], now);
        chunks.ReplaceChunksForMessage(idB, [new TextChunk(0, "beta",  1)], [OneHot(1024, hotIndex: 1)], now);
        chunks.ReplaceChunksForMessage(idC, [new TextChunk(0, "gamma", 1)], [OneHot(1024, hotIndex: 2)], now);

        // Query roughly between alpha and beta but closer to alpha
        var query = OneHot(1024, hotIndex: 0, magnitude: 1f);
        query[1] = 0.5f;

        var hits = search.SearchByVector(query, limit: 10, k: 100);

        hits.Count.ShouldBe(3);
        hits[0].MessageIdHeader.ShouldBe("a@x");   // nearest
        hits[1].MessageIdHeader.ShouldBe("b@x");
        hits[2].MessageIdHeader.ShouldBe("c@x");   // farthest
        hits[0].Distance.ShouldBeLessThan(hits[2].Distance);
    }

    [Fact]
    public void Excludes_soft_deleted_messages()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var search = new VectorSearchService(db.Connections, ollama: null!);
        var now = DateTimeOffset.UtcNow;

        var keepId = messages.Upsert(M("keep@x", "k", "k"), "INBOX", "INBOX/cur", "k", now);
        var dropId = messages.Upsert(M("drop@x", "d", "d"), "INBOX", "INBOX/cur", "d", now);

        chunks.ReplaceChunksForMessage(keepId, [new TextChunk(0, "k", 1)], [OneHot(1024, 0)], now);
        chunks.ReplaceChunksForMessage(dropId, [new TextChunk(0, "d", 1)], [OneHot(1024, 1)], now);

        messages.MarkDeleted([dropId], DateTimeOffset.UtcNow);

        var hits = search.SearchByVector(OneHot(1024, 1), limit: 10, k: 100);

        hits.Count.ShouldBe(1);
        hits[0].MessageIdHeader.ShouldBe("keep@x");
    }

    [Fact]
    public void Returns_best_chunk_per_message_when_a_message_has_multiple()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var search = new VectorSearchService(db.Connections, ollama: null!);
        var now = DateTimeOffset.UtcNow;

        var id = messages.Upsert(M("multi@x", "subj", "body"), "INBOX", "INBOX/cur", "m", now);

        // Three chunks: chunk_index 1 is closest to the query.
        var query = OneHot(1024, hotIndex: 5, magnitude: 1f);
        chunks.ReplaceChunksForMessage(id,
            [new TextChunk(0, "first",  1), new TextChunk(1, "second", 1), new TextChunk(2, "third", 1)],
            [OneHot(1024, hotIndex: 0), OneHot(1024, hotIndex: 5), OneHot(1024, hotIndex: 50)],
            now);

        var hits = search.SearchByVector(query, limit: 10, k: 100);

        hits.Count.ShouldBe(1);                      // one row per message
        hits[0].ChunkIndex.ShouldBe(1);              // and it's the closest chunk
        hits[0].ChunkText.ShouldBe("second");
    }
}
