using Mailvec.Cli.Commands;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Tests;

public class AuditEmbeddingsCommandTests
{
    [Fact]
    public void Empty_database_reports_no_suspicious_vectors()
    {
        using var ctx = new TestServiceProvider();
        var writer = new StringWriter();

        var exit = AuditEmbeddingsCommand.Execute(ctx.Services, sample: 5, normLow: 0.5, normHigh: 1.5, writer);

        exit.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("Scanned 0");
        output.ShouldContain("No suspicious vectors found");
    }

    [Fact]
    public void Healthy_unit_norm_vector_is_not_flagged()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        var chunks = ctx.Services.GetRequiredService<ChunkRepository>();

        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "a", 1)], [HotVector(0)], DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        AuditEmbeddingsCommand.Execute(ctx.Services, sample: 5, normLow: 0.5, normHigh: 1.5, writer);

        var output = writer.ToString();
        output.ShouldContain("All-zero vectors:    0");
        output.ShouldContain("NaN/Inf vectors:     0");
        output.ShouldContain("No suspicious vectors found");
    }

    [Fact]
    public void All_zero_vector_is_flagged_and_sampled()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        var chunks = ctx.Services.GetRequiredService<ChunkRepository>();

        long id = messages.Upsert(Sample("zero@x"), "INBOX", "INBOX/cur", "z", DateTimeOffset.UtcNow);
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "z", 1)], [new float[1024]], DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        AuditEmbeddingsCommand.Execute(ctx.Services, sample: 5, normLow: 0.5, normHigh: 1.5, writer);

        var output = writer.ToString();
        output.ShouldContain("All-zero vectors:    1");
        output.ShouldContain("--- All-zero samples ---");
        output.ShouldContain("msg_id=");
    }

    [Fact]
    public void NaN_vector_is_flagged_and_skips_norm_calculation()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        var chunks = ctx.Services.GetRequiredService<ChunkRepository>();

        long id = messages.Upsert(Sample("nan@x"), "INBOX", "INBOX/cur", "n", DateTimeOffset.UtcNow);
        var nan = new float[1024];
        nan[10] = float.NaN;
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "n", 1)], [nan], DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        AuditEmbeddingsCommand.Execute(ctx.Services, sample: 5, normLow: 0.5, normHigh: 1.5, writer);

        var output = writer.ToString();
        output.ShouldContain("NaN/Inf vectors:     1");
        output.ShouldContain("--- NaN/Inf samples ---");
        output.ShouldContain("norm=  NaN");
    }

    [Fact]
    public void Vectors_outside_the_norm_band_are_flagged_in_their_respective_buckets()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        var chunks = ctx.Services.GetRequiredService<ChunkRepository>();

        // Norm = 0.3 (below 0.5 band)
        long lo = messages.Upsert(Sample("lo@x"), "INBOX", "INBOX/cur", "lo", DateTimeOffset.UtcNow);
        var loVec = new float[1024];
        loVec[0] = 0.3f;
        chunks.ReplaceChunksForMessage(lo, [new TextChunk(0, "lo", 1)], [loVec], DateTimeOffset.UtcNow);

        // Norm = 2.0 (above 1.5 band)
        long hi = messages.Upsert(Sample("hi@x"), "INBOX", "INBOX/cur", "hi", DateTimeOffset.UtcNow);
        var hiVec = new float[1024];
        hiVec[0] = 2.0f;
        chunks.ReplaceChunksForMessage(hi, [new TextChunk(0, "hi", 1)], [hiVec], DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        AuditEmbeddingsCommand.Execute(ctx.Services, sample: 5, normLow: 0.5, normHigh: 1.5, writer);

        var output = writer.ToString();
        output.ShouldContain("Norm < 0.50:        1");
        output.ShouldContain("Norm > 1.50:        1");
    }

    [Fact]
    public void Sample_size_limits_how_many_rows_are_printed_per_category()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        var chunks = ctx.Services.GetRequiredService<ChunkRepository>();

        // Five zero-vector messages; sample=2 should only print two of them.
        for (int i = 0; i < 5; i++)
        {
            long id = messages.Upsert(Sample($"z{i}@x"), "INBOX", "INBOX/cur", $"z{i}", DateTimeOffset.UtcNow);
            chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "z", 1)], [new float[1024]], DateTimeOffset.UtcNow);
        }

        var writer = new StringWriter();
        AuditEmbeddingsCommand.Execute(ctx.Services, sample: 2, normLow: 0.5, normHigh: 1.5, writer);

        var output = writer.ToString();
        output.ShouldContain("All-zero vectors:    5");
        // Sample block prints exactly 2 rows.
        var sampleSection = output[output.IndexOf("--- All-zero samples ---", StringComparison.Ordinal)..];
        var msgIdHits = System.Text.RegularExpressions.Regex.Matches(sampleSection, "msg_id=").Count;
        msgIdHits.ShouldBe(2);
    }

    private static float[] HotVector(int hot, int dim = 1024)
    {
        var v = new float[dim];
        v[hot] = 1f;
        return v;
    }

    private static ParsedMessage Sample(string id) => new(
        MessageId: id,
        ThreadId: id,
        Subject: id,
        FromAddress: "alice@example.com",
        FromName: null,
        ToAddresses: [],
        CcAddresses: [],
        DateSent: DateTimeOffset.UtcNow,
        BodyText: "body",
        BodyHtml: null,
        RawHeaders: $"Message-ID: <{id}>\r\n",
        SizeBytes: 100,
        ContentHash: $"hash-{id}",
        Attachments: []);
}
