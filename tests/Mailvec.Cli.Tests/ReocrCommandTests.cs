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

    // ---- selection order ------------------------------------------------------

    [Fact]
    public void Defaults_to_newest_mail_first()
    {
        // After an engine swap the documents worth re-reading soonest are the
        // ones most likely to be searched. Ordering by attachment id instead
        // would hand back whatever the initial bulk ingest inserted first,
        // which has nothing to do with recency.
        using var ctx = new TestServiceProvider();
        var old = StageDatedPdf(ctx, "old@x", "2019-01-01T00:00:00.0000000+00:00");
        var recent = StageDatedPdf(ctx, "recent@x", "2026-05-01T00:00:00.0000000+00:00");

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: false, limit: 1);

        StatusOf(ctx, recent).ShouldBe(AttachmentTextExtractor.StatusNoText); // reset
        StatusOf(ctx, old).ShouldBe(AttachmentTextExtractor.StatusOcr);       // untouched
    }

    [Fact]
    public void Oldest_order_works_from_the_other_end()
    {
        using var ctx = new TestServiceProvider();
        var old = StageDatedPdf(ctx, "old@x", "2019-01-01T00:00:00.0000000+00:00");
        var recent = StageDatedPdf(ctx, "recent@x", "2026-05-01T00:00:00.0000000+00:00");

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: false,
            limit: 1, order: OcrResetOrder.Oldest);

        StatusOf(ctx, old).ShouldBe(AttachmentTextExtractor.StatusNoText);
        StatusOf(ctx, recent).ShouldBe(AttachmentTextExtractor.StatusOcr);
    }

    [Fact]
    public void Ordering_compares_instants_not_ISO_strings()
    {
        // date_sent is DateTimeOffset.ToString("O"), so ONE column holds mixed
        // offsets. Sorted as text, '...07:13:20-05:00' (12:13Z) lands BELOW
        // '...11:00:00+00:00' (11:00Z) — exactly inverted. Only datetime()
        // normalisation gets this right, and nothing else in the run would
        // reveal the mistake: you would simply re-OCR the wrong documents.
        using var ctx = new TestServiceProvider();
        var laterInstant = StageDatedPdf(ctx, "later@x", "2026-06-28T07:13:20.0000000-05:00");  // 12:13Z
        var earlierInstant = StageDatedPdf(ctx, "earlier@x", "2026-06-28T11:00:00.0000000+00:00"); // 11:00Z

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: false, limit: 1);

        StatusOf(ctx, laterInstant).ShouldBe(AttachmentTextExtractor.StatusNoText);
        StatusOf(ctx, earlierInstant).ShouldBe(AttachmentTextExtractor.StatusOcr);
    }

    [Fact]
    public void Undated_mail_sorts_last_in_both_directions()
    {
        // SQLite puts NULLs first on an ascending sort, so without the explicit
        // IS NULL key "oldest first" would really mean "undated first" — and a
        // --limit run would spend itself on messages with no date at all.
        using var ctx = new TestServiceProvider();
        var undated = StageDatedPdf(ctx, "undated@x", null);
        var dated = StageDatedPdf(ctx, "dated@x", "2019-01-01T00:00:00.0000000+00:00");

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: false,
            limit: 1, order: OcrResetOrder.Oldest);

        StatusOf(ctx, dated).ShouldBe(AttachmentTextExtractor.StatusNoText);
        StatusOf(ctx, undated).ShouldBe(AttachmentTextExtractor.StatusOcr);
    }

    [Fact]
    public void The_report_names_the_order_it_used()
    {
        // With --limit the order decides WHICH documents get done, so it must
        // not be something the operator has to infer.
        using var ctx = new TestServiceProvider();
        StageDatedPdf(ctx, "a@x", "2026-01-01T00:00:00.0000000+00:00");

        var writer = new StringWriter();
        ReocrCommand.Execute(ctx.Services, writer, apply: false, includeFailed: false, limit: 0);

        writer.ToString().ShouldContain("newest mail first");
    }

    /// <summary>An 'ocr' PDF attachment on a message with the given date_sent (null allowed).</summary>
    private static long StageDatedPdf(TestServiceProvider ctx, string messageId, string? dateSentIso)
    {
        var id = StageAttachment(ctx, messageId, "scan.pdf", "application/pdf",
            AttachmentTextExtractor.StatusOcr, "OLD TEXT");
        var mid = MessageIdOf(ctx, id);
        using var conn = ctx.Services.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE messages SET date_sent = $d WHERE id = $id;";
        cmd.Parameters.AddWithValue("$d", (object?)dateSentIso ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", mid);
        cmd.ExecuteNonQuery();
        return id;
    }

    // ---- helpers --------------------------------------------------------------

    [Fact]
    public void Resetting_clears_the_engine_stamp_along_with_the_text_it_described()
    {
        // ocr_model describes extracted_text. reocr nulls the text immediately
        // while replacement only arrives as the passes drain — so a stamp left
        // behind is a provenance claim about text that no longer exists, and if
        // the re-OCR then retires the document it never gets corrected.
        using var ctx = new TestServiceProvider();
        var attId = StageAttachment(ctx, "s@x", "scan.pdf", "application/pdf",
            AttachmentTextExtractor.StatusOcr, "OLD OCR TEXT");
        using (var conn = ctx.Connections.Open())
        using (var stamp = conn.CreateCommand())
        {
            stamp.CommandText = "UPDATE attachments SET ocr_model = 'ollama:old-engine' WHERE id = $id;";
            stamp.Parameters.AddWithValue("$id", attId);
            stamp.ExecuteNonQuery();
        }

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: false, limit: 0)
            .ShouldBe(0);

        using var check = ctx.Connections.Open();
        using var q = check.CreateCommand();
        q.CommandText = "SELECT extracted_text IS NULL, ocr_model IS NULL FROM attachments WHERE id = $id;";
        q.Parameters.AddWithValue("$id", attId);
        using var r = q.ExecuteReader();
        r.Read();
        r.GetBoolean(0).ShouldBeTrue("text should be cleared");
        r.GetBoolean(1).ShouldBeTrue("the engine stamp must be cleared with it");
    }

    [Fact]
    public void The_engine_selector_resets_only_that_engine_s_verdicts()
    {
        // The capability ocr_model exists for. Without it a provider switch can
        // only reset the whole corpus, which is the all-or-nothing behaviour the
        // column was added to end.
        using var ctx = new TestServiceProvider();
        var oldEngine = StageAttachment(ctx, "old@x", "a.pdf", "application/pdf",
            AttachmentTextExtractor.StatusOcr, "OLD");
        var newEngine = StageAttachment(ctx, "new@x", "b.pdf", "application/pdf",
            AttachmentTextExtractor.StatusOcr, "NEW");
        Stamp(ctx, oldEngine, "ollama:qwen2.5vl:7b");
        Stamp(ctx, newEngine, "mistral:mistral-ocr-2505");

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: false,
            limit: 0, engine: "ollama:qwen2.5vl:7b").ShouldBe(0);

        TextOf(ctx, oldEngine).ShouldBeNull("the selected engine's verdict should be reset");
        TextOf(ctx, newEngine).ShouldBe("NEW", "the other engine's work must be left alone");
    }

    [Fact]
    public void The_unknown_selector_targets_rows_with_no_recorded_provenance()
    {
        using var ctx = new TestServiceProvider();
        var unstamped = StageAttachment(ctx, "pre@x", "a.pdf", "application/pdf",
            AttachmentTextExtractor.StatusOcr, "PRE-V10");
        var stamped = StageAttachment(ctx, "post@x", "b.pdf", "application/pdf",
            AttachmentTextExtractor.StatusOcr, "POST");
        Stamp(ctx, stamped, "ollama:qwen2.5vl:7b");

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: false,
            limit: 0, engine: "unknown").ShouldBe(0);

        TextOf(ctx, unstamped).ShouldBeNull();
        TextOf(ctx, stamped).ShouldBe("POST");
    }

    [Fact]
    public void The_selector_is_applied_before_the_limit_not_after()
    {
        // Filtering a slice instead of slicing the filtered set would make
        // --limit 1 return "nothing to do" whenever the newest row happened to
        // belong to the engine you did NOT ask for — a silent no-op that reads
        // as "already done".
        using var ctx = new TestServiceProvider();
        var wanted = StageAttachment(ctx, "wanted@x", "a.pdf", "application/pdf",
            AttachmentTextExtractor.StatusOcr, "WANTED");
        var newest = StageAttachment(ctx, "newest@x", "b.pdf", "application/pdf",
            AttachmentTextExtractor.StatusOcr, "NEWEST");
        Stamp(ctx, wanted, "ollama:old");
        Stamp(ctx, newest, "mistral:new");

        ReocrCommand.Execute(ctx.Services, new StringWriter(), apply: true, includeFailed: false,
            limit: 1, engine: "ollama:old").ShouldBe(0);

        TextOf(ctx, wanted).ShouldBeNull("the one matching row should have been found despite --limit 1");
    }

    private static void Stamp(TestServiceProvider ctx, long attachmentId, string model)
    {
        using var conn = ctx.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE attachments SET ocr_model = $m WHERE id = $id;";
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$id", attachmentId);
        cmd.ExecuteNonQuery();
    }

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
