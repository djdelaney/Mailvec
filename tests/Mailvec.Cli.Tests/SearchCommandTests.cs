using System.Net;
using System.Net.Http.Json;
using Mailvec.Cli.Commands;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Ollama;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Mailvec.Core.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mailvec.Cli.Tests;

public class SearchCommandTests
{
    [Fact]
    public void ParseDate_returns_null_for_blank_input()
    {
        SearchCommand.ParseDate(null, "--date-from").ShouldBeNull();
        SearchCommand.ParseDate("", "--date-from").ShouldBeNull();
        SearchCommand.ParseDate("   ", "--date-from").ShouldBeNull();
    }

    [Fact]
    public void ParseDate_throws_FormatException_for_garbage_input()
    {
        var ex = Should.Throw<FormatException>(() => SearchCommand.ParseDate("not-a-date", "--date-from"));
        ex.Message.ShouldContain("--date-from");
    }

    [Fact]
    public void ParseDate_accepts_ISO_8601_short_form()
    {
        var d = SearchCommand.ParseDate("2024-01-15", "--date-from");
        d.ShouldNotBeNull();
        d!.Value.Year.ShouldBe(2024);
        d.Value.Month.ShouldBe(1);
        d.Value.Day.ShouldBe(15);
    }

    [Fact]
    public async Task ParseAndRun_with_both_semantic_and_hybrid_returns_exit_2()
    {
        var err = new StringWriter();
        var exit = await SearchCommand.ParseAndRun(
            "query", limit: 10, semantic: true, hybrid: true,
            titlesOnly: false, withId: false,
            dateFromRaw: null, dateToRaw: null,
            new StringWriter(), err);

        exit.ShouldBe(2);
        err.ToString().ShouldContain("mutually exclusive");
    }

    [Fact]
    public async Task ParseAndRun_with_bad_date_returns_exit_2_and_writes_to_stderr()
    {
        var err = new StringWriter();
        var exit = await SearchCommand.ParseAndRun(
            "query", limit: 10, semantic: false, hybrid: false,
            titlesOnly: false, withId: false,
            dateFromRaw: "not-a-date", dateToRaw: null,
            new StringWriter(), err);

        exit.ShouldBe(2);
        err.ToString().ShouldContain("not a valid ISO 8601");
    }

    [Fact]
    public async Task Keyword_mode_prints_no_matches_for_empty_db()
    {
        using var ctx = BuildSearchContext(hotQueryIndex: 0);
        var writer = new StringWriter();

        var exit = await SearchCommand.ExecuteAsync(ctx, "ramen",
            limit: 10, semantic: false, hybrid: false, titlesOnly: false, withId: false,
            new SearchFilters(), writer);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("(no matches)");
    }

    [Fact]
    public async Task Keyword_mode_prints_bm25_hits_with_subject_and_snippet()
    {
        using var ctx = BuildSearchContext(hotQueryIndex: 0);
        var messages = ctx.GetRequiredService<MessageRepository>();
        messages.Upsert(Sample("a@x", subject: "Lunch on Friday", body: "ramen at noon"),
            "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        await SearchCommand.ExecuteAsync(ctx, "ramen",
            limit: 10, semantic: false, hybrid: false, titlesOnly: false, withId: false,
            new SearchFilters(), writer);

        var output = writer.ToString();
        output.ShouldContain("1 result(s)");
        output.ShouldContain("bm25");
        output.ShouldContain("Lunch on Friday");
    }

    [Fact]
    public async Task With_id_flag_includes_message_id()
    {
        using var ctx = BuildSearchContext(hotQueryIndex: 0);
        var messages = ctx.GetRequiredService<MessageRepository>();
        messages.Upsert(Sample("traceable@x", body: "ramen"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        await SearchCommand.ExecuteAsync(ctx, "ramen",
            limit: 10, semantic: false, hybrid: false, titlesOnly: false, withId: true,
            new SearchFilters(), writer);

        writer.ToString().ShouldContain("id: traceable@x");
    }

    [Fact]
    public async Task Titles_only_suppresses_snippet_output()
    {
        using var ctx = BuildSearchContext(hotQueryIndex: 0);
        var messages = ctx.GetRequiredService<MessageRepository>();
        messages.Upsert(Sample("a@x", subject: "Subj", body: "ramen tonight"),
            "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var titlesWriter = new StringWriter();
        await SearchCommand.ExecuteAsync(ctx, "ramen",
            limit: 10, semantic: false, hybrid: false, titlesOnly: true, withId: false,
            new SearchFilters(), titlesWriter);

        var fullWriter = new StringWriter();
        await SearchCommand.ExecuteAsync(ctx, "ramen",
            limit: 10, semantic: false, hybrid: false, titlesOnly: false, withId: false,
            new SearchFilters(), fullWriter);

        // Both have the subject; only the full version mentions the snippet's
        // bracketed [ramen] token (KeywordSearchService brackets the FTS hit).
        titlesWriter.ToString().ShouldContain("Subj");
        fullWriter.ToString().ShouldContain("Subj");
        fullWriter.ToString().ShouldContain("[ramen]");
        titlesWriter.ToString().ShouldNotContain("[ramen]");
    }

    [Fact]
    public async Task Semantic_mode_returns_dist_labeled_hits()
    {
        using var ctx = BuildSearchContext(hotQueryIndex: 0);
        var messages = ctx.GetRequiredService<MessageRepository>();
        var chunks = ctx.GetRequiredService<ChunkRepository>();
        long id = messages.Upsert(Sample("a@x", subject: "Subj", body: "body"),
            "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "chunk", 1)], [HotVec(0)], DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        await SearchCommand.ExecuteAsync(ctx, "anything",
            limit: 10, semantic: true, hybrid: false, titlesOnly: false, withId: false,
            new SearchFilters(), writer);

        var output = writer.ToString();
        output.ShouldContain("dist");
        output.ShouldContain("Subj");
    }

    [Fact]
    public async Task Hybrid_mode_returns_rrf_labeled_hits()
    {
        using var ctx = BuildSearchContext(hotQueryIndex: 0);
        var messages = ctx.GetRequiredService<MessageRepository>();
        var chunks = ctx.GetRequiredService<ChunkRepository>();
        long id = messages.Upsert(Sample("a@x", subject: "Subj", body: "ramen tonight"),
            "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "chunk", 1)], [HotVec(0)], DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        await SearchCommand.ExecuteAsync(ctx, "ramen",
            limit: 10, semantic: false, hybrid: true, titlesOnly: false, withId: false,
            new SearchFilters(), writer);

        var output = writer.ToString();
        output.ShouldContain("rrf");
        output.ShouldContain("bm25=");
        output.ShouldContain("vec=");
    }

    // ---------- helpers ----------

    /// <summary>
    /// Builds a ServiceProvider with everything SearchCommand needs: DB
    /// repositories, the three search services, FastmailOptions, and a stub
    /// Ollama that always embeds query strings to a hot vector at the given
    /// index. Returned as the concrete type so tests can <c>using</c> it.
    /// </summary>
    private static ServiceProvider BuildSearchContext(int hotQueryIndex)
    {
        var dbDir = Path.Combine(Path.GetTempPath(), "mailvec-search-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "archive.sqlite");

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ArchiveOptions>(o => o.DatabasePath = dbPath);
        services.Configure<FastmailOptions>(o => o.AccountId = "");
        services.Configure<OllamaOptions>(o =>
        {
            o.EmbeddingDimensions = 1024;
            o.EmbeddingModel = "mxbai-embed-large";
        });

        services.AddSingleton<ConnectionFactory>();
        services.AddSingleton<SchemaMigrator>();
        services.AddSingleton<MessageRepository>();
        services.AddSingleton<MetadataRepository>();
        services.AddSingleton<ChunkRepository>();
        services.AddSingleton<KeywordSearchService>();
        services.AddSingleton(_ => new HotEmbeddingClient(hotQueryIndex));
        services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<HotEmbeddingClient>().Build();
            return new OllamaClient(client, sp.GetRequiredService<IOptions<OllamaOptions>>(), NullLogger<OllamaClient>.Instance);
        });
        services.AddSingleton<VectorSearchService>();
        services.AddSingleton<HybridSearchService>();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        return provider;
    }

    private sealed class HotEmbeddingClient(int hotIndex)
    {
        public HttpClient Build()
        {
            return new HttpClient(new Handler(hotIndex))
            {
                BaseAddress = new Uri("http://localhost:11434"),
            };
        }

        private sealed class Handler(int hotIndex) : HttpMessageHandler
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

    private static float[] HotVec(int hot, int dim = 1024)
    {
        var v = new float[dim];
        v[hot] = 1f;
        return v;
    }

    private static ParsedMessage Sample(string id, string subject = "subj", string body = "body") => new(
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
        ContentHash: $"hash-{id}",
        Attachments: []);
}
