using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Models;
using Mailvec.Core.Parsing;
using Mailvec.Core.Search;
using Mailvec.Core.Tests.Data;

namespace Mailvec.Core.Tests.Search;

/// <summary>
/// SearchFilterSql is internal and shared between keyword and vector search.
/// We exercise it through both services so any filter-semantics drift between
/// the BM25 and vector legs would fail here (hybrid RRF assumes both legs
/// filter identically).
/// </summary>
public class SearchFilterTests
{
    private static ParsedMessage M(
        string id,
        string subject = "subj",
        string body = "ramen body text",
        string? from = "alice@example.com",
        string? fromName = null,
        DateTimeOffset? dateSent = null) => new(
        MessageId: id,
        ThreadId: id,
        Subject: subject,
        FromAddress: from,
        FromName: fromName,
        ToAddresses: [],
        CcAddresses: [],
        DateSent: dateSent ?? DateTimeOffset.UtcNow,
        BodyText: body,
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

    // ---------- Keyword filter tests ----------

    [Fact]
    public void Keyword_filter_by_folder_excludes_other_folders()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var search = new KeywordSearchService(db.Connections);
        var now = DateTimeOffset.UtcNow;

        repo.Upsert(M("a@x"), "INBOX", "INBOX/cur", "a", now);
        repo.Upsert(M("b@x"), "Archive.2024", "Archive.2024/cur", "b", now);

        var hits = search.Search("ramen", filters: new SearchFilters(Folder: "INBOX"));
        hits.Single().MessageIdHeader.ShouldBe("a@x");
    }

    [Fact]
    public void Keyword_filter_by_date_range_inclusive_bounds()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var search = new KeywordSearchService(db.Connections);
        var now = DateTimeOffset.UtcNow;
        var jan = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var feb = new DateTimeOffset(2024, 2, 15, 12, 0, 0, TimeSpan.Zero);
        var mar = new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero);

        repo.Upsert(M("jan@x", dateSent: jan), "INBOX", "INBOX/cur", "j", now);
        repo.Upsert(M("feb@x", dateSent: feb), "INBOX", "INBOX/cur", "f", now);
        repo.Upsert(M("mar@x", dateSent: mar), "INBOX", "INBOX/cur", "m", now);

        var hits = search.Search("ramen", filters: new SearchFilters(
            DateFrom: new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero),
            DateTo: new DateTimeOffset(2024, 2, 28, 23, 59, 59, TimeSpan.Zero)));

        hits.Single().MessageIdHeader.ShouldBe("feb@x");
    }

    [Fact]
    public void Keyword_filter_by_date_handles_mixed_offsets()
    {
        // Stored date_sent is DateTimeOffset.ToString("O") — UTC `Z` and `+HH:mm`
        // mix freely. SearchFilterSql wraps both sides in datetime() to normalize;
        // a raw string compare would silently miss matches across offsets.
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var search = new KeywordSearchService(db.Connections);
        var now = DateTimeOffset.UtcNow;

        var utc   = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var plus2 = new DateTimeOffset(2024, 6, 15, 14, 0, 0, TimeSpan.FromHours(2));   // same instant as utc
        var minus5 = new DateTimeOffset(2024, 6, 15, 7, 0, 0, TimeSpan.FromHours(-5));  // same instant

        repo.Upsert(M("utc@x", dateSent: utc),       "INBOX", "INBOX/cur", "1", now);
        repo.Upsert(M("plus@x", dateSent: plus2),    "INBOX", "INBOX/cur", "2", now);
        repo.Upsert(M("minus@x", dateSent: minus5),  "INBOX", "INBOX/cur", "3", now);

        var hits = search.Search("ramen", filters: new SearchFilters(
            DateFrom: new DateTimeOffset(2024, 6, 15, 11, 0, 0, TimeSpan.Zero),
            DateTo: new DateTimeOffset(2024, 6, 15, 13, 0, 0, TimeSpan.Zero)));

        hits.Count.ShouldBe(3);
    }

    [Fact]
    public void Keyword_date_filter_excludes_messages_with_null_date_sent()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var search = new KeywordSearchService(db.Connections);
        var now = DateTimeOffset.UtcNow;

        repo.Upsert(M("dated@x", dateSent: new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero)),
            "INBOX", "INBOX/cur", "d", now);
        // Null-date message: rebuild ParsedMessage with explicit null DateSent.
        var nullDateMsg = M("undated@x") with { DateSent = null };
        repo.Upsert(nullDateMsg, "INBOX", "INBOX/cur", "u", now);

        var hits = search.Search("ramen", filters: new SearchFilters(
            DateFrom: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        hits.Single().MessageIdHeader.ShouldBe("dated@x");
    }

    [Fact]
    public void Keyword_filter_by_from_exact_is_case_insensitive()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var search = new KeywordSearchService(db.Connections);
        var now = DateTimeOffset.UtcNow;

        repo.Upsert(M("a@x", from: "Invoice@Anthropic.COM"), "INBOX", "INBOX/cur", "a", now);
        repo.Upsert(M("b@x", from: "noreply@anthropic.com"), "INBOX", "INBOX/cur", "b", now);

        var hits = search.Search("ramen", filters: new SearchFilters(FromExact: "invoice@anthropic.com"));
        hits.Single().MessageIdHeader.ShouldBe("a@x");
    }

    [Fact]
    public void Keyword_filter_by_from_contains_matches_address_or_name()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var search = new KeywordSearchService(db.Connections);
        var now = DateTimeOffset.UtcNow;

        repo.Upsert(M("addr@x", from: "bartlett@law.example", fromName: "Counsel"), "INBOX", "INBOX/cur", "a", now);
        repo.Upsert(M("name@x", from: "noreply@x.com", fromName: "Jed Bartlett"),    "INBOX", "INBOX/cur", "n", now);
        repo.Upsert(M("none@x", from: "carol@example.com", fromName: "Carol"),       "INBOX", "INBOX/cur", "o", now);

        var hits = search.Search("ramen", filters: new SearchFilters(FromContains: "bartlett"));

        hits.Select(h => h.MessageIdHeader).ShouldBe(new[] { "addr@x", "name@x" }, ignoreOrder: true);
    }

    [Fact]
    public void Keyword_from_exact_takes_precedence_over_from_contains()
    {
        // CLAUDE.md gotcha: when both are set, FromExact wins (strictly narrower).
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var search = new KeywordSearchService(db.Connections);
        var now = DateTimeOffset.UtcNow;

        repo.Upsert(M("a@x", from: "billing@vendor.com"),       "INBOX", "INBOX/cur", "a", now);
        repo.Upsert(M("b@x", from: "billing-help@vendor.com"),  "INBOX", "INBOX/cur", "b", now);

        var hits = search.Search("ramen", filters: new SearchFilters(
            FromExact: "billing@vendor.com",
            FromContains: "billing"));

        // FromContains alone would return both; FromExact alone returns only a@x.
        // Confirms FromExact wins.
        hits.Single().MessageIdHeader.ShouldBe("a@x");
    }

    [Fact]
    public void Keyword_filters_combine_as_AND()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var search = new KeywordSearchService(db.Connections);
        var now = DateTimeOffset.UtcNow;
        var jan = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var jul = new DateTimeOffset(2024, 7, 15, 12, 0, 0, TimeSpan.Zero);

        repo.Upsert(M("hit@x",       from: "alice@x", dateSent: jul),  "INBOX",   "INBOX/cur", "1", now);
        repo.Upsert(M("wrongdate@x", from: "alice@x", dateSent: jan),  "INBOX",   "INBOX/cur", "2", now);
        repo.Upsert(M("wrongfrom@x", from: "bob@x",   dateSent: jul),  "INBOX",   "INBOX/cur", "3", now);
        repo.Upsert(M("wrongfldr@x", from: "alice@x", dateSent: jul),  "Archive", "Archive/cur", "4", now);

        var hits = search.Search("ramen", filters: new SearchFilters(
            Folder: "INBOX",
            DateFrom: new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero),
            FromExact: "alice@x"));

        hits.Single().MessageIdHeader.ShouldBe("hit@x");
    }

    // ---------- Vector filter tests ----------

    [Fact]
    public void Vector_filter_by_folder_excludes_other_folders()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var search = new VectorSearchService(db.Connections, ollama: null!);
        var now = DateTimeOffset.UtcNow;

        long inboxId   = messages.Upsert(M("a@x"), "INBOX",        "INBOX/cur",        "a", now);
        long archiveId = messages.Upsert(M("b@x"), "Archive.2024", "Archive.2024/cur", "b", now);
        chunks.ReplaceChunksForMessage(inboxId,   [new TextChunk(0, "x", 1)], [OneHot(0)], now);
        chunks.ReplaceChunksForMessage(archiveId, [new TextChunk(0, "x", 1)], [OneHot(0)], now);

        var hits = search.SearchByVector(OneHot(0), limit: 10, k: 100,
            filters: new SearchFilters(Folder: "Archive.2024"));

        hits.Single().MessageIdHeader.ShouldBe("b@x");
    }

    [Fact]
    public void Vector_filter_by_from_contains_matches_substring()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var search = new VectorSearchService(db.Connections, ollama: null!);
        var now = DateTimeOffset.UtcNow;

        long a = messages.Upsert(M("a@x", from: "billing@vendor.com"), "INBOX", "INBOX/cur", "a", now);
        long b = messages.Upsert(M("b@x", from: "noreply@other.com"),  "INBOX", "INBOX/cur", "b", now);
        chunks.ReplaceChunksForMessage(a, [new TextChunk(0, "x", 1)], [OneHot(0)], now);
        chunks.ReplaceChunksForMessage(b, [new TextChunk(0, "x", 1)], [OneHot(1)], now);

        var hits = search.SearchByVector(OneHot(0), limit: 10, k: 100,
            filters: new SearchFilters(FromContains: "vendor"));

        hits.Single().MessageIdHeader.ShouldBe("a@x");
    }

    [Fact]
    public void Vector_filter_by_date_range_excludes_out_of_range()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var search = new VectorSearchService(db.Connections, ollama: null!);
        var now = DateTimeOffset.UtcNow;
        var jan = new DateTimeOffset(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var jul = new DateTimeOffset(2024, 7, 15, 12, 0, 0, TimeSpan.Zero);

        long janId = messages.Upsert(M("jan@x", dateSent: jan), "INBOX", "INBOX/cur", "j", now);
        long julId = messages.Upsert(M("jul@x", dateSent: jul), "INBOX", "INBOX/cur", "u", now);
        chunks.ReplaceChunksForMessage(janId, [new TextChunk(0, "x", 1)], [OneHot(0)], now);
        chunks.ReplaceChunksForMessage(julId, [new TextChunk(0, "x", 1)], [OneHot(0)], now);

        var hits = search.SearchByVector(OneHot(0), limit: 10, k: 100,
            filters: new SearchFilters(
                DateFrom: new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero)));

        hits.Single().MessageIdHeader.ShouldBe("jul@x");
    }

    // ---------- Hybrid integration: filters fire on both legs ----------

    [Fact]
    public async Task Hybrid_filter_applies_identically_to_both_legs()
    {
        using var db = new TempDatabase();
        var messages = new MessageRepository(db.Connections);
        var chunks = new ChunkRepository(db.Connections);
        var keyword = new KeywordSearchService(db.Connections);

        // VectorSearchService.SearchAsync calls ollama.EmbedAsync; bypass that
        // by going through HybridSearchService.Fuse directly with hand-built lists.
        var now = DateTimeOffset.UtcNow;
        long inbox = messages.Upsert(M("inbox@x"),                  "INBOX",   "INBOX/cur",   "i", now);
        long arch  = messages.Upsert(M("arch@x"),                   "Archive", "Archive/cur", "a", now);
        chunks.ReplaceChunksForMessage(inbox, [new TextChunk(0, "x", 1)], [OneHot(0)], now);
        chunks.ReplaceChunksForMessage(arch,  [new TextChunk(0, "x", 1)], [OneHot(0)], now);

        var vectorService = new VectorSearchService(db.Connections, ollama: null!);
        var filters = new SearchFilters(Folder: "Archive");

        var kwHits = keyword.Search("ramen", filters: filters);
        var vecHits = vectorService.SearchByVector(OneHot(0), limit: 50, k: 100, filters: filters);

        kwHits.Single().MessageIdHeader.ShouldBe("arch@x");
        vecHits.Single().MessageIdHeader.ShouldBe("arch@x");

        var fused = HybridSearchService.Fuse(kwHits, vecHits, limit: 10);
        fused.Single().MessageIdHeader.ShouldBe("arch@x");
        await Task.CompletedTask;
    }
}
