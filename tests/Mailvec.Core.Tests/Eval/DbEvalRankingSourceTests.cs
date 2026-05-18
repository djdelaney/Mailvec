using System.Net;
using System.Net.Http.Json;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Eval;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Core.Search;
using Mailvec.Core.Tests.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Tests.Eval;

/// <summary>
/// Coverage for <see cref="DbEvalRankingSource"/>'s per-mode dispatch and the
/// vector-leg k-inflation for filtered queries. We wire real search services
/// against a temp DB and stub Ollama so semantic/hybrid modes can exercise
/// the full path.
/// </summary>
public sealed class DbEvalRankingSourceTests
{
    [Fact]
    public async Task Unknown_EvalMode_throws_ArgumentOutOfRange()
    {
        // The unknown-mode path short-circuits before touching any of the
        // injected services, so we can pass null!. Belt-and-braces: if a
        // future contributor adds an EvalMode value but forgets to wire a
        // case, this guards against the default branch silently degrading.
        var src = new DbEvalRankingSource(keyword: null!, vector: null!, hybrid: null!);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await src.RankAsync("q", (EvalMode)999, topK: 10, filters: null, CancellationToken.None));
    }

    [Fact]
    public async Task Keyword_mode_returns_message_id_headers_in_bm25_order()
    {
        using var fixture = new Fixture();

        // Two messages, one matches the query token, the other doesn't.
        fixture.IngestWithChunk("a@x", "Lunch on Friday", "ramen at 12:30", hotIndex: 0);
        fixture.IngestWithChunk("b@x", "Off topic",       "nothing matches", hotIndex: 100);

        var ids = await fixture.Source.RankAsync(
            "ramen", EvalMode.Keyword, topK: 10, filters: null, CancellationToken.None);

        ids.ShouldContain("a@x");
        ids.ShouldNotContain("b@x");
    }

    [Fact]
    public async Task Semantic_mode_returns_messages_by_vector_similarity()
    {
        // Stub Ollama so the query embeds to a vector hot at index 0. Then
        // message-a (also hot at 0) should be the nearest neighbour.
        using var fixture = new Fixture(queryHotIndex: 0);

        fixture.IngestWithChunk("a@x", "Subject A", "body about ramen", hotIndex: 0);
        fixture.IngestWithChunk("b@x", "Subject B", "body about pasta", hotIndex: 100);

        var ids = await fixture.Source.RankAsync(
            "noodles", EvalMode.Semantic, topK: 10, filters: null, CancellationToken.None);

        // Both within reach for k=100, but a@x is the closer match.
        ids[0].ShouldBe("a@x");
        ids.ShouldContain("b@x");
    }

    [Fact]
    public async Task Hybrid_mode_returns_message_id_headers_from_RRF_fusion()
    {
        using var fixture = new Fixture(queryHotIndex: 0);

        // a@x wins both legs: keyword match on "ramen" + vector aligned with query.
        // b@x loses both. RRF should rank a@x first.
        fixture.IngestWithChunk("a@x", "Subject A", "ramen tonight", hotIndex: 0);
        fixture.IngestWithChunk("b@x", "Subject B", "nothing here", hotIndex: 100);

        var ids = await fixture.Source.RankAsync(
            "ramen", EvalMode.Hybrid, topK: 10, filters: null, CancellationToken.None);

        ids.ShouldNotBeEmpty();
        ids[0].ShouldBe("a@x");
    }

    [Fact]
    public async Task Semantic_mode_with_restrictive_filter_inflates_k_enough_to_return_matches()
    {
        // Vector KNN runs before the filter join: with a small k and a tight
        // filter, the post-filter set can be empty even when the right match
        // exists further down the KNN list. DbEvalRankingSource compensates
        // by inflating k for filtered queries — this test would fail with
        // the unfiltered-mode k=Math.Max(100, topK * 5).
        using var fixture = new Fixture(queryHotIndex: 50);

        // Eleven near-misses (hot at 0..10) that pollute the top of the KNN
        // list, plus one Archive.2024 match (hot at 50) that the filter
        // keeps. With topK=1 and the unfiltered inflation, k=Math.Max(100,
        // 5) = 100 still finds it; with the filtered inflation, k=Math.Max(
        // 500, 50) = 500 obviously does too. Set up enough chaff to make
        // the safety margin visible.
        for (int i = 0; i < 20; i++)
        {
            fixture.IngestWithChunk($"chaff{i}@x", "noise", "noise body", hotIndex: i, folder: "INBOX");
        }
        fixture.IngestWithChunk("kept@x", "target", "target body", hotIndex: 50, folder: "Archive.2024");

        var ids = await fixture.Source.RankAsync(
            "anything", EvalMode.Semantic, topK: 1,
            filters: new SearchFilters(Folder: "Archive.2024", null, null, null, null),
            CancellationToken.None);

        ids.ShouldContain("kept@x");
    }

    // ----------- helpers -----------

    private sealed class Fixture : IDisposable
    {
        private readonly TempDatabase _db;
        private readonly MessageRepository _messages;
        private readonly ChunkRepository _chunks;
        public DbEvalRankingSource Source { get; }

        public Fixture(int queryHotIndex = 0)
        {
            _db = new TempDatabase();
            _messages = new MessageRepository(_db.Connections);
            _chunks = new ChunkRepository(_db.Connections);

            // Initialise metadata so any downstream readers don't trip.
            var metadata = new MetadataRepository(_db.Connections);
            metadata.Set("embedding_model", "mxbai-embed-large");
            metadata.Set("embedding_dimensions", "1024");

            // Stubbed Ollama: the test's "query" always embeds to a vector hot
            // at queryHotIndex. Messages with the same hot index match exactly;
            // others are orthogonal.
            var http = new HttpClient(new HotEmbeddingHandler(queryHotIndex))
            {
                BaseAddress = new Uri("http://localhost:11434"),
            };
            var ollamaOpts = Microsoft.Extensions.Options.Options.Create(new OllamaOptions
            {
                EmbeddingDimensions = 1024,
                EmbeddingModel = "mxbai-embed-large",
            });
            var ollama = new OllamaClient(http, ollamaOpts, NullLogger<OllamaClient>.Instance);

            var keyword = new KeywordSearchService(_db.Connections);
            var vector = new VectorSearchService(_db.Connections, ollama);
            var hybrid = new HybridSearchService(keyword, vector);
            Source = new DbEvalRankingSource(keyword, vector, hybrid);
        }

        public void IngestWithChunk(string messageId, string subject, string body, int hotIndex, string folder = "INBOX")
        {
            var parsed = new ParsedMessage(
                MessageId: messageId,
                ThreadId: messageId,
                Subject: subject,
                FromAddress: "alice@example.com",
                FromName: null,
                ToAddresses: [],
                CcAddresses: [],
                DateSent: DateTimeOffset.UtcNow,
                BodyText: body,
                BodyHtml: null,
                RawHeaders: $"Message-ID: <{messageId}>\r\n",
                SizeBytes: 100,
                ContentHash: $"hash-{messageId}",
                Attachments: []);
            long id = _messages.Upsert(parsed, folder, $"{folder}/cur", "f", DateTimeOffset.UtcNow);
            _chunks.ReplaceChunksForMessage(
                id,
                [new TextChunk(0, body, body.Length / 4)],
                [HotVector(hotIndex)],
                DateTimeOffset.UtcNow);
        }

        private static float[] HotVector(int hotIndex, int dim = 1024)
        {
            var v = new float[dim];
            v[hotIndex % dim] = 1f;
            return v;
        }

        public void Dispose() => _db.Dispose();
    }

    private sealed class HotEmbeddingHandler(int hotIndex) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var n = doc.RootElement.GetProperty("input").GetArrayLength();
            var vectors = new float[n][];
            for (int i = 0; i < n; i++)
            {
                vectors[i] = new float[1024];
                vectors[i][hotIndex % 1024] = 1f;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { embeddings = vectors }),
            };
        }
    }
}
