using Mailvec.Cli.Commands;
using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Models;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Tests;

/// <summary>
/// Tests for the smaller CLI commands that benefit from a thin Execute seam:
/// status, rebuild-fts, reindex, repair, get.
/// </summary>
public class SimpleCommandsTests
{
    // ---------------- StatusCommand ----------------

    [Fact]
    public void Status_against_empty_db_prints_zero_counts()
    {
        using var ctx = new TestServiceProvider();
        ctx.AddOption<IngestOptions>(o => o.MaildirRoot = "/tmp/mail");
        ctx.AddOption<OllamaOptions>(o =>
        {
            o.EmbeddingModel = "mxbai-embed-large";
            o.EmbeddingDimensions = 1024;
        });
        var sp = ctx.Rebuild();

        var writer = new StringWriter();
        var exit = StatusCommand.Execute(sp, writer);

        exit.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("Messages:    0 total");
        output.ShouldContain("Embeddings:  0");
        output.ShouldContain("mxbai-embed-large");
    }

    [Fact]
    public void Status_shows_the_ocr_backlog_when_scanned_pdfs_await()
    {
        using var ctx = new TestServiceProvider();
        ctx.AddOption<IngestOptions>(o => o.MaildirRoot = "/tmp/mail");
        ctx.AddOption<OllamaOptions>(o => { o.EmbeddingModel = "mxbai-embed-large"; o.EmbeddingDimensions = 1024; });
        var sp = ctx.Rebuild();
        sp.GetRequiredService<MessageRepository>().Upsert(
            new ParsedMessage("scan@x", "scan@x", "s", "a@x", null, [], [], DateTimeOffset.UtcNow, "b", null,
                "Message-ID: <scan@x>\r\n", 100, "h",
                [new ParsedAttachment(0, "scan.pdf", "application/pdf", 100, ExtractedText: null, ExtractionStatus: "no_text")]),
            "INBOX", "INBOX/cur", "scan.eml", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        StatusCommand.Execute(sp, writer);

        writer.ToString().ShouldContain("OCR pending: 1 scanned PDF");
    }

    [Fact]
    public void Status_emits_mismatch_warning_when_schema_model_disagrees_with_config()
    {
        using var ctx = new TestServiceProvider();
        ctx.AddOption<IngestOptions>(o => o.MaildirRoot = "/tmp/mail");
        ctx.AddOption<OllamaOptions>(o =>
        {
            o.EmbeddingModel = "mxbai-embed-large";
            o.EmbeddingDimensions = 1024;
        });
        var sp = ctx.Rebuild();
        sp.GetRequiredService<MetadataRepository>().Set("embedding_model", "nomic-embed-text");
        sp.GetRequiredService<MetadataRepository>().Set("embedding_dimensions", "768");

        var writer = new StringWriter();
        StatusCommand.Execute(sp, writer);

        var output = writer.ToString();
        output.ShouldContain("Schema/config mismatch");
        output.ShouldContain("reindex");
    }

    // ---------------- RebuildFtsCommand ----------------

    [Fact]
    public void RebuildFts_prints_confirmation_and_keeps_search_working()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        messages.Upsert(Sample("a@x", subject: "Lunch on Friday", body: "ramen at noon"),
            "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var exit = RebuildFtsCommand.Execute(ctx.Services, writer);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("FTS5 index rebuilt");

        // FTS5 still works post-rebuild.
        var keyword = new Mailvec.Core.Search.KeywordSearchService(ctx.Connections);
        keyword.Search("ramen").ShouldHaveSingleItem();
    }

    // ---------------- ReindexCommand ----------------

    [Fact]
    public void Reindex_with_neither_all_nor_folder_returns_exit_2_and_writes_to_stderr()
    {
        var err = new StringWriter();
        var exit = ReindexCommand.ValidateAndRun(all: false, folder: null, yes: false, new StringWriter(), err, readLine: () => null);

        exit.ShouldBe(2);
        err.ToString().ShouldContain("--all");
    }

    [Fact]
    public void Reindex_with_all_clears_embeddings_for_every_message()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        var chunks = ctx.Services.GetRequiredService<ChunkRepository>();

        long inbox = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);
        long arch = messages.Upsert(Sample("b@x"), "Archive.2024", "Archive.2024/cur", "b", DateTimeOffset.UtcNow);
        chunks.ReplaceChunksForMessage(inbox, [new TextChunk(0, "a", 1)], [HotVec(0)], DateTimeOffset.UtcNow);
        chunks.ReplaceChunksForMessage(arch, [new TextChunk(0, "b", 1)], [HotVec(1)], DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var exit = ReindexCommand.Execute(ctx.Services, folder: null, yes: true, writer, readLine: () => null);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("Cleared embeddings on 2");
        chunks.CountForMessage(inbox).ShouldBe(0);
        chunks.CountForMessage(arch).ShouldBe(0);
    }

    [Fact]
    public void Reindex_with_folder_only_clears_that_folder()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        var chunks = ctx.Services.GetRequiredService<ChunkRepository>();

        long inbox = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);
        long arch = messages.Upsert(Sample("b@x"), "Archive.2024", "Archive.2024/cur", "b", DateTimeOffset.UtcNow);
        chunks.ReplaceChunksForMessage(inbox, [new TextChunk(0, "a", 1)], [HotVec(0)], DateTimeOffset.UtcNow);
        chunks.ReplaceChunksForMessage(arch, [new TextChunk(0, "b", 1)], [HotVec(1)], DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var exit = ReindexCommand.Execute(ctx.Services, folder: "INBOX", yes: true, writer, readLine: () => null);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("folder 'INBOX'");
        chunks.CountForMessage(inbox).ShouldBe(0);
        chunks.CountForMessage(arch).ShouldBe(1);  // archive untouched
    }

    [Fact]
    public void Reindex_aborts_when_user_declines_the_prompt()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        var chunks = ctx.Services.GetRequiredService<ChunkRepository>();
        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "a", 1)], [HotVec(0)], DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var exit = ReindexCommand.Execute(ctx.Services, folder: null, yes: false, writer, readLine: () => "n");

        exit.ShouldBe(1);
        writer.ToString().ShouldContain("Aborted");
        chunks.CountForMessage(id).ShouldBe(1);   // nothing cleared
    }

    [Fact]
    public void Reindex_proceeds_when_user_confirms_at_prompt()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        var chunks = ctx.Services.GetRequiredService<ChunkRepository>();
        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "a", 1)], [HotVec(0)], DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var exit = ReindexCommand.Execute(ctx.Services, folder: null, yes: false, writer, readLine: () => " Y ");

        exit.ShouldBe(0);
        chunks.CountForMessage(id).ShouldBe(0);
    }

    // ---------------- RepairCommand ----------------

    [Fact]
    public void Repair_clean_db_reports_no_repairs_needed()
    {
        using var ctx = new TestServiceProvider();
        var writer = new StringWriter();
        var exit = RepairCommand.Execute(ctx.Services, dryRun: false, writer);

        exit.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain("Orphan vectors");
        output.ShouldContain("none");
        output.ShouldContain("No repairs needed");
    }

    [Fact]
    public void Repair_with_orphans_deletes_them()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        var chunks = ctx.Services.GetRequiredService<ChunkRepository>();

        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "a", 1)], [HotVec(0)], DateTimeOffset.UtcNow);

        // Manually leak orphan: delete chunks row but leave the embedding.
        using (var conn = ctx.Connections.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM chunks WHERE message_id = $mid";
            cmd.Parameters.AddWithValue("$mid", id);
            cmd.ExecuteNonQuery();
        }
        chunks.CountOrphanEmbeddings().ShouldBe(1);

        var writer = new StringWriter();
        var exit = RepairCommand.Execute(ctx.Services, dryRun: false, writer);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("deleted 1");
        chunks.CountOrphanEmbeddings().ShouldBe(0);
    }

    [Fact]
    public void Repair_dry_run_reports_but_does_not_delete()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        var chunks = ctx.Services.GetRequiredService<ChunkRepository>();

        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);
        chunks.ReplaceChunksForMessage(id, [new TextChunk(0, "a", 1)], [HotVec(0)], DateTimeOffset.UtcNow);
        using (var conn = ctx.Connections.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM chunks WHERE message_id = $mid";
            cmd.Parameters.AddWithValue("$mid", id);
            cmd.ExecuteNonQuery();
        }

        var writer = new StringWriter();
        var exit = RepairCommand.Execute(ctx.Services, dryRun: true, writer);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("would repair 1");
        chunks.CountOrphanEmbeddings().ShouldBe(1);  // not deleted
    }

    // ---------------- GetCommand ----------------

    [Fact]
    public void Get_by_numeric_id_prints_message_details()
    {
        using var ctx = new TestServiceProvider();
        ctx.AddOption<FastmailOptions>(o => o.AccountId = "");
        var sp = ctx.Rebuild();

        var messages = sp.GetRequiredService<MessageRepository>();
        long id = messages.Upsert(Sample("a@x", subject: "Test subject", body: "Body content"),
            "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var err = new StringWriter();
        var exit = GetCommand.Execute(sp, id.ToString(), fullBody: false, writer, err);

        exit.ShouldBe(0);
        var output = writer.ToString();
        output.ShouldContain($"id:        {id}");
        output.ShouldContain("a@x");
        output.ShouldContain("Test subject");
        output.ShouldContain("Body content");
    }

    [Fact]
    public void Get_by_rfc_message_id_strips_angle_brackets()
    {
        using var ctx = new TestServiceProvider();
        ctx.AddOption<FastmailOptions>(o => o.AccountId = "");
        var sp = ctx.Rebuild();

        var messages = sp.GetRequiredService<MessageRepository>();
        messages.Upsert(Sample("brackets@x", subject: "Subj", body: "Body"),
            "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var err = new StringWriter();
        // Header-style with angle brackets — should strip and look up by stored bare form.
        var exit = GetCommand.Execute(sp, "<brackets@x>", fullBody: false, writer, err);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("brackets@x");
    }

    [Fact]
    public void Get_with_unknown_id_writes_to_stderr_and_exits_1()
    {
        using var ctx = new TestServiceProvider();
        ctx.AddOption<FastmailOptions>(o => o.AccountId = "");
        var sp = ctx.Rebuild();

        var writer = new StringWriter();
        var err = new StringWriter();
        var exit = GetCommand.Execute(sp, "999", fullBody: false, writer, err);

        exit.ShouldBe(1);
        err.ToString().ShouldContain("No message found");
    }

    [Fact]
    public void Get_truncates_long_body_unless_full_body_flag_is_set()
    {
        using var ctx = new TestServiceProvider();
        ctx.AddOption<FastmailOptions>(o => o.AccountId = "");
        var sp = ctx.Rebuild();

        var messages = sp.GetRequiredService<MessageRepository>();
        var longBody = new string('a', 2000);
        long id = messages.Upsert(Sample("a@x", subject: "S", body: longBody),
            "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        // Without --body: should truncate
        var truncated = new StringWriter();
        GetCommand.Execute(sp, id.ToString(), fullBody: false, truncated, new StringWriter());
        truncated.ToString().ShouldContain("…(truncated");

        // With --body: full content
        var full = new StringWriter();
        GetCommand.Execute(sp, id.ToString(), fullBody: true, full, new StringWriter());
        full.ToString().ShouldNotContain("…(truncated");
    }

    [Fact]
    public void Get_emits_webmail_url_when_Fastmail_account_configured()
    {
        using var ctx = new TestServiceProvider();
        ctx.AddOption<FastmailOptions>(o => o.AccountId = "u12345678");
        var sp = ctx.Rebuild();

        var messages = sp.GetRequiredService<MessageRepository>();
        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        GetCommand.Execute(sp, id.ToString(), fullBody: false, writer, new StringWriter());

        writer.ToString().ShouldContain("webmail:");
        writer.ToString().ShouldContain("u12345678");
    }

    // ---------------- helpers ----------------

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
