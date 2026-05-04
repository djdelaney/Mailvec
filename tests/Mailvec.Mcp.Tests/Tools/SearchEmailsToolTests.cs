using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Models;
using Mailvec.Core.Options;
using Mailvec.Core.Search;
using Mailvec.Mcp.Tools;
using ModelContextProtocol;

namespace Mailvec.Mcp.Tests.Tools;

public class SearchEmailsToolTests
{
    private static SearchEmailsTool Build(
        TempDatabase db,
        Mailvec.Core.Ollama.OllamaClient? ollama = null,
        McpOptions? mcpOpts = null,
        FastmailOptions? fastmailOpts = null)
    {
        var messages = new MessageRepository(db.Connections);
        var keyword = new KeywordSearchService(db.Connections);
        var vector = new VectorSearchService(db.Connections, ollama!);
        var hybrid = new HybridSearchService(keyword, vector);
        return new SearchEmailsTool(
            keyword, vector, hybrid, messages,
            Helpers.Mcp(mcpOpts),
            Helpers.Fastmail(fastmailOpts),
            Helpers.NoopLogger());
    }

    // ---------- Browse path (no query) ----------

    [Fact]
    public async Task Browse_path_returns_filter_matched_messages_sorted_by_date_desc()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;
        repo.Upsert(Helpers.Sample("old@x", dateSent: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            "INBOX", "INBOX/cur", "o", now);
        repo.Upsert(Helpers.Sample("new@x", dateSent: new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero)),
            "INBOX", "INBOX/cur", "n", now);

        var tool = Build(db);
        var resp = await tool.SearchEmails(query: null);

        resp.Mode.ShouldBe("browse");
        resp.Count.ShouldBe(2);
        resp.Results[0].MessageId.ShouldBe("new@x");   // newest first
        resp.Results[1].MessageId.ShouldBe("old@x");
    }

    [Fact]
    public async Task Browse_path_emits_archive_stats_and_applied_filters()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        repo.Upsert(Helpers.Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var tool = Build(db);
        var resp = await tool.SearchEmails(query: null, folder: "INBOX", fromExact: "Alice@Example.com");

        resp.AppliedFilters.Folder.ShouldBe("INBOX");
        resp.AppliedFilters.FromExact.ShouldBe("Alice@Example.com");
        resp.ArchiveStats.TotalMessages.ShouldBe(1);
    }

    // ---------- Mode dispatch ----------

    [Fact]
    public async Task Mode_keyword_uses_BM25_and_populates_score()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        repo.Upsert(Helpers.Sample("a@x", body: "ramen friday"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var tool = Build(db);
        var resp = await tool.SearchEmails(query: "ramen", mode: "keyword");

        resp.Mode.ShouldBe("keyword");
        resp.Results.Single().Bm25Score.ShouldNotBeNull();
        resp.Results.Single().RrfScore.ShouldBeNull();
    }

    [Fact]
    public async Task Mode_semantic_uses_vector_search_and_populates_distance()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;
        long id = repo.Upsert(Helpers.Sample("a@x"), "INBOX", "INBOX/cur", "a", now);
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "x", 1)], [Helpers.OneHot(0)], now);

        var tool = Build(db, ollama: Helpers.StubOllama(hotIndex: 0));
        var resp = await tool.SearchEmails(query: "anything", mode: "semantic");

        resp.Mode.ShouldBe("semantic");
        resp.Results.Single().VectorDistance.ShouldNotBeNull();
        resp.Results.Single().Bm25Score.ShouldBeNull();
    }

    [Fact]
    public async Task Mode_hybrid_populates_rrf_score_and_ranks()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;
        long id = repo.Upsert(Helpers.Sample("a@x", body: "ramen friday"), "INBOX", "INBOX/cur", "a", now);
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "ramen", 1)], [Helpers.OneHot(0)], now);

        var tool = Build(db, ollama: Helpers.StubOllama(hotIndex: 0));
        var resp = await tool.SearchEmails(query: "ramen", mode: "hybrid");

        resp.Mode.ShouldBe("hybrid");
        var hit = resp.Results.Single();
        hit.RrfScore.ShouldNotBeNull();
        hit.Bm25Rank.ShouldBe(1);
        hit.VectorRank.ShouldBe(1);
    }

    [Fact]
    public async Task Mode_normalisation_is_case_insensitive_and_trimmed()
    {
        using var db = new TempDatabase();
        new MessageRepository(db.Connections).Upsert(Helpers.Sample("a@x", body: "ramen"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var tool = Build(db);
        var resp = await tool.SearchEmails(query: "ramen", mode: "  KEYWORD ");

        resp.Mode.ShouldBe("keyword");
    }

    [Fact]
    public async Task Unknown_mode_throws_McpException()
    {
        using var db = new TempDatabase();
        var tool = Build(db);

        var ex = await Should.ThrowAsync<McpException>(
            () => tool.SearchEmails(query: "x", mode: "wat"));
        ex.Message.ShouldContain("wat");
    }

    // ---------- Date parsing ----------

    [Fact]
    public async Task DateFrom_accepts_ISO_8601_date_only()
    {
        using var db = new TempDatabase();
        var tool = Build(db);

        var resp = await tool.SearchEmails(query: null, dateFrom: "2024-01-01");

        resp.AppliedFilters.DateFrom.ShouldNotBeNull();
        resp.AppliedFilters.DateFrom.ShouldStartWith("2024-01-01");
    }

    [Fact]
    public async Task Bad_date_throws_McpException_naming_the_field()
    {
        using var db = new TempDatabase();
        var tool = Build(db);

        var ex = await Should.ThrowAsync<McpException>(
            () => tool.SearchEmails(query: null, dateTo: "not-a-date"));
        ex.Message.ShouldContain("dateTo");
    }

    // ---------- Limit clamping ----------

    [Fact]
    public async Task Limit_is_clamped_to_SearchMaxLimit_when_exceeded()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        for (int i = 0; i < 10; i++)
            repo.Upsert(Helpers.Sample($"m{i}@x"), "INBOX", "INBOX/cur", $"m{i}", DateTimeOffset.UtcNow);

        var tool = Build(db, mcpOpts: new McpOptions { SearchMaxLimit = 3, SearchDefaultLimit = 20 });
        var resp = await tool.SearchEmails(query: null, limit: 999);

        resp.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Limit_below_one_is_clamped_up_to_one()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        for (int i = 0; i < 5; i++)
            repo.Upsert(Helpers.Sample($"m{i}@x"), "INBOX", "INBOX/cur", $"m{i}", DateTimeOffset.UtcNow);

        var tool = Build(db);
        var resp = await tool.SearchEmails(query: null, limit: 0);

        resp.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Limit_null_uses_SearchDefaultLimit()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        for (int i = 0; i < 10; i++)
            repo.Upsert(Helpers.Sample($"m{i}@x"), "INBOX", "INBOX/cur", $"m{i}", DateTimeOffset.UtcNow);

        var tool = Build(db, mcpOpts: new McpOptions { SearchDefaultLimit = 4, SearchMaxLimit = 100 });
        var resp = await tool.SearchEmails(query: null, limit: null);

        resp.Count.ShouldBe(4);
    }

    // ---------- Webmail link decoration ----------

    [Fact]
    public async Task Webmail_url_is_emitted_when_AccountId_configured()
    {
        using var db = new TempDatabase();
        new MessageRepository(db.Connections).Upsert(Helpers.Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var tool = Build(db, fastmailOpts: new FastmailOptions { AccountId = "u12345678" });
        var resp = await tool.SearchEmails(query: null);

        var url = resp.Results.Single().WebmailUrl;
        url.ShouldNotBeNull();
        url.ShouldContain("u12345678");
    }

    [Fact]
    public async Task Webmail_url_is_null_when_AccountId_unset()
    {
        using var db = new TempDatabase();
        new MessageRepository(db.Connections).Upsert(Helpers.Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var tool = Build(db);
        var resp = await tool.SearchEmails(query: null);

        resp.Results.Single().WebmailUrl.ShouldBeNull();
    }

    // ---------- AppliedFilters round-trip ----------

    [Fact]
    public void AppliedFilters_From_serialises_dates_in_round_trip_format()
    {
        var f = new SearchFilters(
            Folder: "INBOX",
            DateFrom: new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
            FromContains: "vendor");

        var applied = AppliedFilters.From(f);

        applied.Folder.ShouldBe("INBOX");
        applied.DateFrom.ShouldBe("2024-06-01T00:00:00.0000000+00:00");
        applied.FromContains.ShouldBe("vendor");
        applied.DateTo.ShouldBeNull();
        applied.FromExact.ShouldBeNull();
    }

    // ---------- EmailHit factories ----------

    [Fact]
    public void EmailHit_FromMessage_truncates_long_bodies_and_collapses_whitespace()
    {
        var msg = new Message
        {
            Id = 1,
            MessageId = "m@x",
            MaildirPath = "INBOX/cur",
            MaildirFilename = "1.eml",
            Folder = "INBOX",
            BodyText = "  line one\n\n  line   two   " + new string('x', 300),
        };

        var hit = EmailHit.FromMessage(msg);

        hit.Snippet.ShouldNotContain("\n");
        hit.Snippet.ShouldNotContain("   ");
        hit.Snippet.Length.ShouldBeLessThanOrEqualTo(241);   // 240 + ellipsis
        hit.Snippet.ShouldEndWith("…");
    }

    [Fact]
    public void EmailHit_FromMessage_handles_null_or_blank_body()
    {
        var msg = new Message { Id = 1, MessageId = "m@x", MaildirPath = "p", MaildirFilename = "f", Folder = "INBOX", BodyText = null };

        EmailHit.FromMessage(msg).Snippet.ShouldBe(string.Empty);
    }
}
