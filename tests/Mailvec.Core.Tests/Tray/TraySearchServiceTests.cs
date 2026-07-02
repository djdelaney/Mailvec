using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Core.Search;
using Mailvec.Core.Tests.Data;
using Mailvec.Core.Tray;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Tests.Tray;

/// <summary>
/// Black-box tests for <see cref="TraySearchService"/>. The service wraps the
/// same KeywordSearchService / VectorSearchService / HybridSearchService the
/// MCP search_emails tool uses; we mostly need to verify it maps results into
/// the flat <see cref="TraySearchHit"/> shape correctly, applies the
/// query-less browse path when query is missing, clamps limits, and attaches
/// webmail URLs when Fastmail is configured.
/// </summary>
public class TraySearchServiceTests
{
    [Fact]
    public async Task Empty_query_returns_browse_results_sorted_by_date()
    {
        await using var ctx = new Setup();

        // Three messages in chronological order; browse path returns latest first.
        ctx.InsertMessage("a@x", "first",  "body one",   DateTimeOffset.Parse("2025-01-01T10:00:00Z"));
        ctx.InsertMessage("b@x", "second", "body two",   DateTimeOffset.Parse("2025-02-01T10:00:00Z"));
        ctx.InsertMessage("c@x", "third",  "body three", DateTimeOffset.Parse("2025-03-01T10:00:00Z"));

        var resp = await ctx.Service.SearchAsync(new TraySearchRequest(
            Query: null, Mode: "hybrid", Limit: 10,
            Folder: null, DateFrom: null, DateTo: null,
            FromContains: null, FromExact: null));

        resp.Mode.ShouldBe("browse");
        resp.Count.ShouldBe(3);
        resp.Results[0].MessageId.ShouldBe("c@x");
        resp.Results[2].MessageId.ShouldBe("a@x");
        // Score should be monotone-descending across browse rows.
        resp.Results[0].Score.ShouldBeGreaterThan(resp.Results[2].Score);
    }

    [Fact]
    public async Task Keyword_mode_returns_bm25_scores()
    {
        await using var ctx = new Setup();

        ctx.InsertMessage("a@x", "Lunch on Friday", "ramen at 12:30",  null);
        ctx.InsertMessage("b@x", "Hello",           "ramen sounds great", null);
        ctx.InsertMessage("c@x", "Off topic",       "no match here",   null);

        var resp = await ctx.Service.SearchAsync(new TraySearchRequest(
            Query: "ramen", Mode: "keyword", Limit: 10,
            Folder: null, DateFrom: null, DateTo: null,
            FromContains: null, FromExact: null));

        resp.Mode.ShouldBe("keyword");
        resp.Count.ShouldBe(2);
        resp.Results.ShouldAllBe(r => r.Bm25Score != null);
        resp.Results.ShouldAllBe(r => r.VectorScore == null);
    }

    [Fact]
    public async Task Keyword_scores_are_normalized_into_unit_range_with_best_hit_at_one()
    {
        // FTS5 bm25() is negative (more negative = better). The tray's score
        // bar multiplies by Score, so values must land in (0,1] with the top
        // hit at 1.0 — dividing by the WORST score used to give every row a
        // ratio >= 1 and all bars rendered full.
        await using var ctx = new Setup();

        ctx.InsertMessage("a@x", "ramen ramen ramen", "ramen ramen ramen ramen", null); // strong match
        ctx.InsertMessage("b@x", "Hello", "one mention of ramen in a much longer body about other things entirely", null);

        var resp = await ctx.Service.SearchAsync(new TraySearchRequest(
            Query: "ramen", Mode: "keyword", Limit: 10,
            Folder: null, DateFrom: null, DateTo: null,
            FromContains: null, FromExact: null));

        resp.Count.ShouldBe(2);
        resp.Results.ShouldAllBe(r => r.Score > 0 && r.Score <= 1.0);
        resp.Results[0].Score.ShouldBe(1.0, tolerance: 1e-9);       // best hit pegs the bar
        resp.Results[1].Score.ShouldBeLessThan(resp.Results[0].Score); // weaker hit scales down
    }

    [Fact]
    public async Task Limit_is_clamped_to_McpOptions_bounds()
    {
        await using var ctx = new Setup();

        // Insert five messages. SearchDefaultLimit=20, but request limit=2 → 2 hits.
        for (int i = 0; i < 5; i++)
        {
            ctx.InsertMessage($"m{i}@x", $"subject {i}", "body", DateTimeOffset.UtcNow.AddMinutes(-i));
        }

        var resp = await ctx.Service.SearchAsync(new TraySearchRequest(
            Query: null, Mode: null, Limit: 2,
            Folder: null, DateFrom: null, DateTo: null,
            FromContains: null, FromExact: null));

        resp.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Limit_below_one_is_clamped_to_one()
    {
        await using var ctx = new Setup();
        ctx.InsertMessage("a@x", "subject", "body", DateTimeOffset.UtcNow);

        var resp = await ctx.Service.SearchAsync(new TraySearchRequest(
            Query: null, Mode: null, Limit: 0,
            Folder: null, DateFrom: null, DateTo: null,
            FromContains: null, FromExact: null));

        resp.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_mode_throws()
    {
        await using var ctx = new Setup();
        ctx.InsertMessage("a@x", "hi", "body", DateTimeOffset.UtcNow);

        await Should.ThrowAsync<ArgumentException>(() => ctx.Service.SearchAsync(new TraySearchRequest(
            Query: "hi", Mode: "magic", Limit: 10,
            Folder: null, DateFrom: null, DateTo: null,
            FromContains: null, FromExact: null)));
    }

    [Fact]
    public async Task Folder_filter_passes_through_to_browse_path()
    {
        await using var ctx = new Setup();

        ctx.InsertMessage("inbox@x",  "in",     "body", DateTimeOffset.UtcNow, folder: "INBOX");
        ctx.InsertMessage("arch@x",   "out",    "body", DateTimeOffset.UtcNow, folder: "Archive.2024");

        var resp = await ctx.Service.SearchAsync(new TraySearchRequest(
            Query: null, Mode: null, Limit: 20,
            Folder: "Archive.2024", DateFrom: null, DateTo: null,
            FromContains: null, FromExact: null));

        resp.Count.ShouldBe(1);
        resp.Results[0].MessageId.ShouldBe("arch@x");
    }

    [Fact]
    public async Task WebmailUrl_is_emitted_when_Fastmail_account_configured()
    {
        await using var ctx = new Setup(fastmailAccountId: "u12345678");
        ctx.InsertMessage("a@x", "hi", "body", DateTimeOffset.UtcNow);

        var resp = await ctx.Service.SearchAsync(new TraySearchRequest(
            Query: null, Mode: null, Limit: 10,
            Folder: null, DateFrom: null, DateTo: null,
            FromContains: null, FromExact: null));

        resp.Results[0].WebmailUrl.ShouldNotBeNull();
        resp.Results[0].WebmailUrl!.ShouldContain("u12345678");
        resp.Results[0].WebmailUrl!.ShouldContain("msgid:");
    }

    [Fact]
    public async Task WebmailUrl_is_null_when_Fastmail_account_not_configured()
    {
        await using var ctx = new Setup();
        ctx.InsertMessage("a@x", "hi", "body", DateTimeOffset.UtcNow);

        var resp = await ctx.Service.SearchAsync(new TraySearchRequest(
            Query: null, Mode: null, Limit: 10,
            Folder: null, DateFrom: null, DateTo: null,
            FromContains: null, FromExact: null));

        resp.Results[0].WebmailUrl.ShouldBeNull();
    }

    [Fact]
    public async Task Browse_snippet_is_collapsed_and_truncated()
    {
        await using var ctx = new Setup();
        // 300 chars including newlines/whitespace that the snippet collapses.
        var body = string.Join("\n\n", Enumerable.Repeat("paragraph", 50));
        ctx.InsertMessage("a@x", "subj", body, DateTimeOffset.UtcNow);

        var resp = await ctx.Service.SearchAsync(new TraySearchRequest(
            Query: null, Mode: null, Limit: 10,
            Folder: null, DateFrom: null, DateTo: null,
            FromContains: null, FromExact: null));

        // 240-char cap + ellipsis.
        resp.Results[0].Snippet.Length.ShouldBeLessThanOrEqualTo(241);
        // Internal newlines should have been replaced by spaces.
        resp.Results[0].Snippet.ShouldNotContain("\n");
    }

    [Fact]
    public async Task Default_mode_is_hybrid()
    {
        await using var ctx = new Setup();
        // No messages; hybrid leg returns 0 hits. We just verify the mode echo.
        var resp = await ctx.Service.SearchAsync(new TraySearchRequest(
            Query: "anything", Mode: null, Limit: 10,
            Folder: null, DateFrom: null, DateTo: null,
            FromContains: null, FromExact: null));

        resp.Mode.ShouldBe("hybrid");
        resp.Count.ShouldBe(0);
    }

    // ---------- helpers ----------

    private sealed class Setup : IAsyncDisposable
    {
        private readonly TempDatabase _db;
        public TraySearchService Service { get; }
        public MessageRepository Repo { get; }

        public Setup(string fastmailAccountId = "")
        {
            _db = new TempDatabase();
            Repo = new MessageRepository(_db.Connections);
            var keyword = new KeywordSearchService(_db.Connections);
            // VectorSearchService needs an OllamaClient — we won't actually call
            // semantic mode in these tests, but we still need a non-null instance
            // for hybrid mode when there are zero messages (hybrid runs both
            // legs). Wire a stub HttpClient that returns empty embeddings to
            // keep the path inert.
            var http = new HttpClient(new EmptyHandler())
            {
                BaseAddress = new Uri("http://localhost:11434"),
            };
            var ollamaOpts = Microsoft.Extensions.Options.Options.Create(new OllamaOptions { EmbeddingDimensions = 1024 });
            var ollama = new Mailvec.Core.Ollama.OllamaClient(http, ollamaOpts, NullLogger<Mailvec.Core.Ollama.OllamaClient>.Instance);
            var vector = new VectorSearchService(_db.Connections, ollama);
            var hybrid = new HybridSearchService(keyword, vector);

            var mcpOpts = Microsoft.Extensions.Options.Options.Create(new McpOptions
            {
                SearchDefaultLimit = 20,
                SearchMaxLimit = 100,
            });
            var fastmailOpts = Microsoft.Extensions.Options.Options.Create(new FastmailOptions { AccountId = fastmailAccountId });

            Service = new TraySearchService(keyword, vector, hybrid, Repo, mcpOpts, fastmailOpts);
        }

        public void InsertMessage(string messageId, string subject, string body, DateTimeOffset? dateSent, string folder = "INBOX")
        {
            // BrowseByFilters requires non-null date_sent for ordering to work
            // sensibly; supply UtcNow if the caller didn't.
            var parsed = new ParsedMessage(
                MessageId: messageId,
                ThreadId: messageId,
                Subject: subject,
                FromAddress: "alice@example.com",
                FromName: null,
                ToAddresses: [],
                CcAddresses: [],
                DateSent: dateSent ?? DateTimeOffset.UtcNow,
                BodyText: body,
                BodyHtml: null,
                RawHeaders: $"Message-ID: <{messageId}>\r\n",
                SizeBytes: 100,
                ContentHash: $"hash-{messageId}",
                Attachments: []);
            Repo.Upsert(parsed, folder, $"{folder}/cur", "f", DateTimeOffset.UtcNow);
        }

        public ValueTask DisposeAsync()
        {
            _db.Dispose();
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Returns one zero-vector per input string so OllamaClient's input/output
        /// count check passes. The vector search then runs but finds no matches
        /// (the test DB has zero chunks), which is the inert state we want.
        /// </summary>
        private sealed class EmptyHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var body = await request.Content!.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var n = doc.RootElement.GetProperty("input").GetArrayLength();
                var vectors = Enumerable.Range(0, n).Select(_ => new float[1024]).ToArray();
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = System.Net.Http.Json.JsonContent.Create(new { embeddings = vectors }),
                };
            }
        }
    }
}
