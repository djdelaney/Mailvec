using Mailvec.Cli.Commands;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Parsing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Tests;

public class ReocrCommandTests
{
    [Fact]
    public void Reports_nothing_to_do_on_a_corpus_with_no_ocr_verdicts()
    {
        using var ctx = new TestServiceProvider();
        var writer = new StringWriter();

        ReocrCommand.Execute(ctx.Services, writer, apply: true, includeFailed: false, limit: 0).ShouldBe(0);

        writer.ToString().ShouldContain("Nothing to re-OCR");
    }

    [Fact]
    public void Dry_run_is_the_default_and_changes_nothing()
    {
        // Resetting clears text immediately while replacements only arrive as the
        // embedder drains the backlog — against a broken provider that is an
        // outage. The plan must be printable without committing to it.
        using var ctx = new TestServiceProvider();
        var id = StageAttachment(ctx, "a@x", "scan.pdf", "application/pdf",
            AttachmentTextExtractor.StatusOcr, "OLD ENGINE TEXT");

        var writer = new StringWriter();
        ReocrCommand.Execute(ctx.Services, writer, apply: false, includeFailed: false, limit: 0).ShouldBe(0);

        writer.ToString().ShouldContain("Dry run");
        StatusOf(ctx, id).ShouldBe(AttachmentTextExtractor.StatusOcr);
        TextOf(ctx, id).ShouldBe("OLD ENGINE TEXT");
    }

    [Fact]
    public void Ocr_pdf_returns_to_no_text_so_the_pdf_pass_selects_it_again()
    {
        // The whole point: 'ocr' is matched by neither pass's predicate, so
        // without this the old engine's output is permanent.
        using var ctx = new TestServiceProvider();
        var id = StageAttachment(ctx, "a@x", "scan.pdf", "application/pdf",
            AttachmentTextExtractor.StatusOcr, "HALLUCINATED FRONT VIEW");

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: false, limit: 0).ShouldBe(0);

        StatusOf(ctx, id).ShouldBe(AttachmentTextExtractor.StatusNoText);
        TextOf(ctx, id).ShouldBeNull();
    }

    [Fact]
    public void Ocr_image_returns_to_unsupported_so_the_image_pass_selects_it_again()
    {
        // Different pass, different predicate, different target status. Sending
        // an image back to 'no_text' would orphan it — neither pass selects that.
        using var ctx = new TestServiceProvider();
        var id = StageAttachment(ctx, "a@x", "photo.jpg", "image/jpeg",
            AttachmentTextExtractor.StatusOcr, "SOME TEXT");

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: false, limit: 0).ShouldBe(0);

        StatusOf(ctx, id).ShouldBe(AttachmentTextExtractor.StatusUnsupported);
    }

    [Fact]
    public void An_image_the_old_engine_ruled_textless_is_reconsidered()
    {
        // 'no_text' on an image is the old engine's verdict that there was
        // nothing to read — exactly the call a different engine might make
        // differently, and selectable by nothing until reset.
        using var ctx = new TestServiceProvider();
        var id = StageAttachment(ctx, "a@x", "photo.jpg", "image/jpeg",
            AttachmentTextExtractor.StatusNoText, null);

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: false, limit: 0).ShouldBe(0);

        StatusOf(ctx, id).ShouldBe(AttachmentTextExtractor.StatusUnsupported);
    }

    [Fact]
    public void Natively_extracted_text_is_never_touched()
    {
        // 'done' text came from the indexer reading a real PDF text layer. It is
        // better than any OCR of the same page, and re-OCRing it would replace
        // good text with worse — silently, since nothing would flag the swap.
        using var ctx = new TestServiceProvider();
        var id = StageAttachment(ctx, "a@x", "invoice.pdf", "application/pdf",
            AttachmentTextExtractor.StatusDone, "REAL TEXT LAYER");

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: false, limit: 0);

        StatusOf(ctx, id).ShouldBe(AttachmentTextExtractor.StatusDone);
        TextOf(ctx, id).ShouldBe("REAL TEXT LAYER");
    }

    [Fact]
    public void Failed_is_opt_in()
    {
        // 'failed' also covers non-OCR extraction failures (corrupt .eml,
        // unopenable PDF) that a new vision provider will not fix.
        using var ctx = new TestServiceProvider();
        var id = StageAttachment(ctx, "a@x", "broken.pdf", "application/pdf",
            AttachmentTextExtractor.StatusFailed, null);

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: false, limit: 0);
        StatusOf(ctx, id).ShouldBe(AttachmentTextExtractor.StatusFailed);

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: true, limit: 0);
        StatusOf(ctx, id).ShouldBe(AttachmentTextExtractor.StatusNoText);
    }

    [Fact]
    public void The_reset_rebuilds_attachment_text_and_requeues_in_one_step()
    {
        // Two invariants at once. Clearing extracted_text without rebuilding
        // messages.attachment_text leaves the FTS column asserting text no
        // attachment contains; clearing embedded_at without bumping embed_epoch
        // lets an in-flight embed stamp over the re-queue, pinning vectors to
        // the OLD OCR text with no hash delta to ever re-trigger.
        using var ctx = new TestServiceProvider();
        var attachmentId = StageAttachment(ctx, "a@x", "scan.pdf", "application/pdf",
            AttachmentTextExtractor.StatusOcr, "OLD ENGINE TEXT");
        var messageId = MessageIdOf(ctx, attachmentId);

        // Simulate a fully-embedded message carrying the old OCR text in FTS.
        Exec(ctx, $"UPDATE messages SET attachment_text = 'OLD ENGINE TEXT', embedded_at = '2026-01-01T00:00:00Z' WHERE id = {messageId};");
        var epochBefore = ScalarLong(ctx, $"SELECT embed_epoch FROM messages WHERE id = {messageId};");

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: false, limit: 0);

        ScalarString(ctx, $"SELECT attachment_text FROM messages WHERE id = {messageId};").ShouldBeNull();
        ScalarString(ctx, $"SELECT embedded_at FROM messages WHERE id = {messageId};").ShouldBeNull();
        ScalarLong(ctx, $"SELECT embed_epoch FROM messages WHERE id = {messageId};").ShouldBe(epochBefore + 1);
    }

    [Fact]
    public void Sibling_attachment_text_survives_the_rebuild()
    {
        // attachment_text is a space-join over ALL the message's attachments.
        // Resetting one must not wipe a sibling's still-valid text out of FTS.
        using var ctx = new TestServiceProvider();
        var scanId = StageAttachment(ctx, "a@x", "scan.pdf", "application/pdf",
            AttachmentTextExtractor.StatusOcr, "OCR TEXT", partIndex: 0);
        var messageId = MessageIdOf(ctx, scanId);
        Exec(ctx, $"""
            INSERT INTO attachments (message_id, part_index, filename, content_type, size_bytes, extracted_text, extraction_status)
            VALUES ({messageId}, 1, 'contract.pdf', 'application/pdf', 10, 'NATIVE TEXT', '{AttachmentTextExtractor.StatusDone}');
            """);

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: false, limit: 0);

        ScalarString(ctx, $"SELECT attachment_text FROM messages WHERE id = {messageId};").ShouldBe("NATIVE TEXT");
    }

    [Fact]
    public void Limit_slices_a_large_backlog()
    {
        using var ctx = new TestServiceProvider();
        StageAttachment(ctx, "a@x", "one.pdf", "application/pdf", AttachmentTextExtractor.StatusOcr, "T1");
        StageAttachment(ctx, "b@x", "two.pdf", "application/pdf", AttachmentTextExtractor.StatusOcr, "T2");
        StageAttachment(ctx, "c@x", "three.pdf", "application/pdf", AttachmentTextExtractor.StatusOcr, "T3");

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: false, limit: 2);

        ScalarLong(ctx, $"SELECT COUNT(*) FROM attachments WHERE extraction_status = '{AttachmentTextExtractor.StatusOcr}';")
            .ShouldBe(1);
    }

    // ---- helpers --------------------------------------------------------------

    private static long StageAttachment(
        TestServiceProvider ctx, string messageId, string filename, string contentType,
        string status, string? text, int partIndex = 0)
    {
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        long mid = messages.Upsert(Sample(messageId), "INBOX", "INBOX/cur", messageId, DateTimeOffset.UtcNow);

        using var conn = ctx.Services.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO attachments (message_id, part_index, filename, content_type, size_bytes, extracted_text, extraction_status)
            VALUES ($mid, $pi, $fn, $ct, 100, $text, $status)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$mid", mid);
        cmd.Parameters.AddWithValue("$pi", partIndex);
        cmd.Parameters.AddWithValue("$fn", filename);
        cmd.Parameters.AddWithValue("$ct", contentType);
        cmd.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", status);
        return (long)cmd.ExecuteScalar()!;
    }

    private static long MessageIdOf(TestServiceProvider ctx, long attachmentId) =>
        ScalarLong(ctx, $"SELECT message_id FROM attachments WHERE id = {attachmentId};");

    private static string? StatusOf(TestServiceProvider ctx, long attachmentId) =>
        ScalarString(ctx, $"SELECT extraction_status FROM attachments WHERE id = {attachmentId};");

    private static string? TextOf(TestServiceProvider ctx, long attachmentId) =>
        ScalarString(ctx, $"SELECT extracted_text FROM attachments WHERE id = {attachmentId};");

    private static void Exec(TestServiceProvider ctx, string sql)
    {
        using var conn = ctx.Services.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string? ScalarString(TestServiceProvider ctx, string sql)
    {
        using var conn = ctx.Services.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : (string)v;
    }

    private static long ScalarLong(TestServiceProvider ctx, string sql)
    {
        using var conn = ctx.Services.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
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
