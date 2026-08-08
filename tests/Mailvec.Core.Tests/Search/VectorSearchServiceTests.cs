using Mailvec.Core.Attachments;
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
        ContentHash: $"test-hash-{id}",
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
        var search = new VectorSearchService(db.Connections, embeddings: null!);   // unused — we use SearchByVector

        var now = DateTimeOffset.UtcNow;
        long idA = messages.Upsert(M("a@x", "Alpha topic", "alpha body text"), "INBOX", "INBOX/cur", "a", now);
        long idB = messages.Upsert(M("b@x", "Beta topic",  "beta body text"),  "INBOX", "INBOX/cur", "b", now);
        long idC = messages.Upsert(M("c@x", "Gamma topic", "gamma body text"), "INBOX", "INBOX/cur", "c", now);

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
        var search = new VectorSearchService(db.Connections, embeddings: null!);
        var now = DateTimeOffset.UtcNow;

        long keepId = messages.Upsert(M("keep@x", "k", "k"), "INBOX", "INBOX/cur", "k", now);
        long dropId = messages.Upsert(M("drop@x", "d", "d"), "INBOX", "INBOX/cur", "d", now);

        chunks.ReplaceChunksForMessage(keepId, [new TextChunk(0, "k", 1)], [OneHot(1024, 0)], now);
        chunks.ReplaceChunksForMessage(dropId, [new TextChunk(0, "d", 1)], [OneHot(1024, 1)], now);

        messages.MarkDeleted([dropId], DateTimeOffset.UtcNow);

        var hits = search.SearchByVector(OneHot(1024, 1), limit: 10, k: 100);

        hits.Count.ShouldBe(1);
        hits[0].MessageIdHeader.ShouldBe("keep@x");
    }

    [Fact]
    public void Surfaces_matched_attachment_when_top_chunk_came_from_attachment()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var search = new VectorSearchService(db.Connections, embeddings: null!);
        var now = DateTimeOffset.UtcNow;

        var withAttachment = M("att@x", "Subject", "thin body") with
        {
            Attachments = [new Mailvec.Core.Parsing.ParsedAttachment(
                PartIndex: 0,
                FileName: "report.pdf",
                ContentType: "application/pdf",
                SizeBytes: 1234,
                ExtractedText: "extracted PDF content",
                ExtractionStatus: AttachmentTextExtractor.StatusDone)]
        };
        long id = messages.Upsert(withAttachment, "INBOX", "INBOX/cur", "f1", now);

        // Look up the persisted attachment id so we can pair the chunk with it.
        var msg = messages.GetById(id).ShouldNotBeNull();
        var attId = msg.Attachments.Single().Id;

        // Body chunk hot on dim 0; attachment chunk hot on dim 1. Query
        // closest to dim 1 should return the attachment-chunk match and
        // expose the attachment metadata on the hit.
        chunks.ReplaceChunksForMessage(id,
            [
                new TextChunk(0, "body", 1, Source: "body", AttachmentId: null),
                new TextChunk(1, "extracted", 1, Source: "attachment", AttachmentId: attId),
            ],
            [OneHot(1024, hotIndex: 0), OneHot(1024, hotIndex: 1)],
            now);

        var hits = search.SearchByVector(OneHot(1024, hotIndex: 1), limit: 5, k: 100);

        hits.Count.ShouldBe(1);
        hits[0].ChunkSource.ShouldBe("attachment");
        hits[0].MatchedAttachmentId.ShouldBe(attId);
        hits[0].MatchedAttachmentPartIndex.ShouldBe(0);
        hits[0].MatchedAttachmentFileName.ShouldBe("report.pdf");
    }

    [Fact]
    public void Escalates_k_so_a_restrictive_folder_filter_is_not_starved()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var search = new VectorSearchService(db.Connections, embeddings: null!);
        var now = DateTimeOffset.UtcNow;

        // 60 INBOX "chaff" messages whose chunks are the nearest to the query
        // (one-hot on dims 0..59). The 3 target messages live in Archive.2024 and
        // are FAR from the query (dims 500..502) — i.e. well beyond a small k.
        for (var i = 0; i < 60; i++)
        {
            long cid = messages.Upsert(M($"chaff{i}@x", "noise", "noise"), "INBOX", "INBOX/cur", $"c{i}", now);
            chunks.ReplaceChunksForMessage(cid, [new TextChunk(0, "noise", 1)], [OneHot(1024, hotIndex: i)], now);
        }
        for (var i = 0; i < 3; i++)
        {
            long tid = messages.Upsert(M($"target{i}@x", "target", "target"), "Archive.2024", "Archive.2024/cur", $"t{i}", now);
            chunks.ReplaceChunksForMessage(tid, [new TextChunk(0, "target", 1)], [OneHot(1024, hotIndex: 500 + i)], now);
        }

        var query = OneHot(1024, hotIndex: 0);   // nearest neighbours are all INBOX chaff
        var filters = new SearchFilters(Folder: "Archive.2024");

        // Base k=5 is far smaller than the ~60 chaff chunks that rank ahead of the
        // targets. A single-shot KNN would return 0 in-folder hits; escalation must
        // widen k until the 3 Archive.2024 targets surface.
        var hits = search.SearchByVector(query, limit: 3, k: 5, filters);

        hits.Count.ShouldBe(3);
        hits.ShouldAllBe(h => h.Folder == "Archive.2024");
    }

    [Fact]
    public void Escalates_k_when_soft_deleted_messages_crowd_the_nearest_neighbours()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var search = new VectorSearchService(db.Connections, embeddings: null!);
        var now = DateTimeOffset.UtcNow;

        // 60 soft-deleted messages own the nearest chunks; 3 live targets sit
        // far down the ranking. Nothing purges soft-deletes automatically
        // (purge-deleted is a manual CLI command), so this is the steady state
        // of a delete-heavy corpus. The old unfiltered path did a single-shot
        // KNN: the deleted_at IS NULL predicate dropped every neighbour and
        // returned 0 hits even though live matches existed.
        var deletedIds = new List<long>();
        for (var i = 0; i < 60; i++)
        {
            long id = messages.Upsert(M($"dead{i}@x", "noise", "noise"), "INBOX", "INBOX/cur", $"d{i}", now);
            chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "noise", 1)], [OneHot(1024, hotIndex: i)], now);
            deletedIds.Add(id);
        }
        messages.MarkDeleted(deletedIds, now);
        for (var i = 0; i < 3; i++)
        {
            long id = messages.Upsert(M($"live{i}@x", "target", "target"), "INBOX", "INBOX/cur", $"l{i}", now);
            chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "target", 1)], [OneHot(1024, hotIndex: 500 + i)], now);
        }

        var query = OneHot(1024, hotIndex: 0);   // nearest neighbours are all soft-deleted

        // No explicit filters — this exercises the formerly short-circuited path.
        var hits = search.SearchByVector(query, limit: 3, k: 5);

        hits.Count.ShouldBe(3);
        hits.ShouldAllBe(h => h.MessageIdHeader.StartsWith("live"));
    }

    [Fact]
    public void Returns_best_chunk_per_message_when_a_message_has_multiple()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var search = new VectorSearchService(db.Connections, embeddings: null!);
        var now = DateTimeOffset.UtcNow;

        long id = messages.Upsert(M("multi@x", "subj", "body"), "INBOX", "INBOX/cur", "m", now);

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

    [Fact]
    public async Task Query_instruction_prefix_is_prepended_to_query_embeds()
    {
        // The prefix now rides the resolved profile through the REAL
        // EmbeddingService — this pins that a profile prefix reaches the
        // wire through the search path, with no way to bypass it here.
        using var db = new TempDatabase();
        var fake = new CapturingEmbeddingClient();
        var search = new VectorSearchService(db.Connections, new EmbeddingService(
            fake, Tests.Embedding.TestProfiles.Legacy(queryPrefix: "Instruct: retrieve passages\nQuery: ")));

        await search.SearchAsync("kids haircut barber", limit: 5);

        fake.LastInputs.ShouldNotBeNull();
        fake.LastInputs![0].ShouldBe("Instruct: retrieve passages\nQuery: kids haircut barber");
    }

    [Fact]
    public async Task Empty_prefix_embeds_the_bare_query()
    {
        using var db = new TempDatabase();
        var fake = new CapturingEmbeddingClient();
        var search = new VectorSearchService(db.Connections, new EmbeddingService(
            fake, Tests.Embedding.TestProfiles.Legacy()));

        await search.SearchAsync("kids haircut barber", limit: 5);

        fake.LastInputs![0].ShouldBe("kids haircut barber");
    }

    // ---------- read-side embedding-space guard ----------

    [Fact]
    public async Task Semantic_search_refuses_every_form_of_metadata_identity_drift()
    {
        // The write side refuses drift five ways; before this guard the read
        // side served through it — an edited query prefix (same model, same
        // dims) produced plausible, meaningless rankings with no error
        // anywhere. Each drift axis must refuse BEFORE embedding.
        using var db = new TempDatabase();
        var search = GuardedSearch(db);

        // Matching identity: searches fine.
        await Should.NotThrowAsync(() => search.SearchAsync("hello", limit: 5));

        foreach (var (key, drifted) in new[]
        {
            ("embedding_model", "some-other-model"),
            (Mailvec.Core.Embedding.EmbeddingSpace.SpaceIdKey, "ollama:some-other-model:1024"),
            (Mailvec.Core.Embedding.EmbeddingSpace.ConfigHashKey, "0000000000000000000000000000000000000000000000000000000000000000"),
        })
        {
            var original = Metadata(db, key);
            SetMetadata(db, key, drifted);
            var ex = await Should.ThrowAsync<Mailvec.Core.Embedding.EmbeddingException>(
                () => search.SearchAsync("hello", limit: 5));
            ex.Kind.ShouldBe(Mailvec.Core.Embedding.EmbeddingFailureKind.SpaceMismatch, key);
            ex.Message.ShouldContain(key);
            SetMetadata(db, key, original!);
        }
    }

    [Fact]
    public async Task Hybrid_search_refuses_through_its_vector_leg()
    {
        using var db = new TempDatabase();
        SetMetadata(db, Mailvec.Core.Embedding.EmbeddingSpace.SpaceIdKey, "ollama:drifted:1024");
        var hybrid = new HybridSearchService(new KeywordSearchService(db.Connections), GuardedSearch(db));

        var ex = await Should.ThrowAsync<Mailvec.Core.Embedding.EmbeddingException>(
            () => hybrid.SearchAsync("hello", limit: 5));
        ex.Kind.ShouldBe(Mailvec.Core.Embedding.EmbeddingFailureKind.SpaceMismatch);
    }

    [Fact]
    public async Task Absent_identity_metadata_is_unknown_and_passes_the_guard()
    {
        // A fresh database has no vectors for a wrong answer to come from;
        // unknown ≠ mismatch, same as everywhere else.
        using var db = new TempDatabase();
        DeleteMetadata(db, Mailvec.Core.Embedding.EmbeddingSpace.SpaceIdKey);
        DeleteMetadata(db, Mailvec.Core.Embedding.EmbeddingSpace.ConfigHashKey);

        await Should.NotThrowAsync(() => GuardedSearch(db).SearchAsync("hello", limit: 5));
    }

    private static VectorSearchService GuardedSearch(TempDatabase db) => new(
        db.Connections,
        new EmbeddingService(new CapturingEmbeddingClient(), Tests.Embedding.TestProfiles.Legacy()),
        new Mailvec.Core.Embedding.EmbeddingSpaceGuard(
            new MetadataRepository(db.Connections), Tests.Embedding.TestProfiles.Legacy()));

    private static string? Metadata(TempDatabase db, string key)
    {
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM metadata WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetMetadata(TempDatabase db, string key, string value)
    {
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO metadata(key, value) VALUES($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static void DeleteMetadata(TempDatabase db, string key)
    {
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM metadata WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    private sealed class CapturingEmbeddingClient : IEmbeddingTransport
    {
        public IReadOnlyList<string>? LastInputs;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
        {
            LastInputs = inputs;
            return Task.FromResult(new[] { OneHot(1024, hotIndex: 0) });
        }

        public Task<bool?> IsModelAvailableAsync(CancellationToken ct = default) => Task.FromResult<bool?>(true);
    }
}
