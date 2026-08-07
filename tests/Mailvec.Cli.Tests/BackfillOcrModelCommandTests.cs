using Mailvec.Cli.Commands;
using Mailvec.Core.Attachments;
using Microsoft.Data.Sqlite;

namespace Mailvec.Cli.Tests;

/// <summary>
/// The operator-asserted backfill for <c>attachments.ocr_model</c>.
///
/// The interesting behaviour is all about what it REFUSES to touch: rows at or
/// after the cutoff (which the OCR passes must stamp themselves, from
/// observation), rows already stamped, and rows whose status was never an OCR
/// verdict. A backfill that over-reaches is worse than none, because a wrong
/// engine id makes "re-OCR everything engine X produced" silently skip the real
/// X rows and re-run the other engine's.
/// </summary>
public class BackfillOcrModelCommandTests
{
    private const string OldEngine = "ollama:qwen2.5vl:7b";

    [Fact]
    public void Dry_run_is_the_default_and_writes_nothing()
    {
        using var ctx = new TestServiceProvider();
        var id = StageOcr(ctx, "a@x", "2026-08-01T10:00:00Z");
        var writer = new StringWriter();

        BackfillOcrModelCommand.Execute(ctx.Services, writer, OldEngine, "2026-08-06", apply: false, overwrite: false)
            .ShouldBe(0);

        writer.ToString().ShouldContain("Dry run");
        ModelOf(ctx, id).ShouldBeNull();
    }

    [Fact]
    public void Stamps_only_rows_strictly_before_the_cutoff()
    {
        // The whole point of the cutoff: everything the NEW engine produced must
        // stay NULL so a re-OCR can stamp it from observation rather than
        // inheriting the operator's assertion about the old one.
        using var ctx = new TestServiceProvider();
        var before = StageOcr(ctx, "old@x", "2026-08-01T10:00:00Z");
        var after = StageOcr(ctx, "new@x", "2026-08-07T10:00:00Z");
        var writer = new StringWriter();

        BackfillOcrModelCommand.Execute(ctx.Services, writer, OldEngine, "2026-08-06", apply: true, overwrite: false)
            .ShouldBe(0);

        ModelOf(ctx, before).ShouldBe(OldEngine);
        ModelOf(ctx, after).ShouldBeNull();
    }

    [Fact]
    public void The_cutoff_compares_instants_not_ISO_strings()
    {
        // extracted_at is DateTimeOffset.ToString("O"), so one column holds mixed
        // offsets. Sorted as TEXT, '2026-08-05T21:00:00-05:00' (02:00Z on the
        // 6th) sorts BELOW '2026-08-06T01:00:00+00:00' — inverted. Both rows here
        // are genuinely before the 03:00Z cutoff as instants; a string compare
        // would disagree about the offset one.
        using var ctx = new TestServiceProvider();
        var offsetRow = StageOcr(ctx, "off@x", "2026-08-05T21:00:00-05:00"); // = 02:00Z on the 6th
        var utcRow = StageOcr(ctx, "utc@x", "2026-08-06T01:00:00+00:00");
        var writer = new StringWriter();

        BackfillOcrModelCommand.Execute(ctx.Services, writer, OldEngine, "2026-08-06T03:00:00Z", apply: true, overwrite: false)
            .ShouldBe(0);

        ModelOf(ctx, offsetRow).ShouldBe(OldEngine);
        ModelOf(ctx, utcRow).ShouldBe(OldEngine);
    }

    [Fact]
    public void An_observed_stamp_wins_over_the_assertion_unless_overwrite_is_given()
    {
        // A value the OCR pass wrote is a fact recorded at write time; this
        // command's input is an operator's recollection. The fact wins by default.
        using var ctx = new TestServiceProvider();
        var id = StageOcr(ctx, "stamped@x", "2026-08-01T10:00:00Z", model: "mistral:mistral-ocr-2505");
        var writer = new StringWriter();

        BackfillOcrModelCommand.Execute(ctx.Services, writer, OldEngine, "2026-08-06", apply: true, overwrite: false);
        ModelOf(ctx, id).ShouldBe("mistral:mistral-ocr-2505");

        BackfillOcrModelCommand.Execute(ctx.Services, new StringWriter(), OldEngine, "2026-08-06", apply: true, overwrite: true);
        ModelOf(ctx, id).ShouldBe(OldEngine);
    }

    [Fact]
    public void Never_stamps_a_status_that_was_not_an_OCR_verdict()
    {
        // 'done' is native extraction and 'no_text' is also what the INDEXER
        // writes for a scanned PDF it couldn't read — neither is evidence that a
        // vision engine ever looked at the row, so neither may be back-stamped.
        using var ctx = new TestServiceProvider();
        var native = StageOcr(ctx, "native@x", "2026-08-01T10:00:00Z", status: AttachmentTextExtractor.StatusDone);
        var noText = StageOcr(ctx, "notext@x", "2026-08-01T10:00:00Z", status: AttachmentTextExtractor.StatusNoText);

        BackfillOcrModelCommand.Execute(ctx.Services, new StringWriter(), OldEngine, "2026-08-06", apply: true, overwrite: false);

        ModelOf(ctx, native).ShouldBeNull();
        ModelOf(ctx, noText).ShouldBeNull();
    }

    [Fact]
    public void A_malformed_cutoff_is_rejected_rather_than_silently_matching_nothing()
    {
        using var ctx = new TestServiceProvider();
        var writer = new StringWriter();

        BackfillOcrModelCommand.Execute(ctx.Services, writer, OldEngine, "last tuesday", apply: true, overwrite: false)
            .ShouldBe(2);

        writer.ToString().ShouldContain("not a valid ISO 8601");
    }

    // ---- helpers ----

    private static long StageOcr(
        TestServiceProvider ctx, string messageId, string extractedAt,
        string? model = null, string? status = null)
    {
        using var conn = ctx.Connections.Open();
        using var m = conn.CreateCommand();
        m.CommandText = """
            INSERT INTO messages (message_id, subject, from_address, date_sent, indexed_at, folder,
                                  maildir_path, maildir_filename, size_bytes)
            VALUES ($mid, 's', 'a@x', '2026-08-01T00:00:00+00:00', '2026-08-01T00:00:00+00:00',
                    'INBOX', 'INBOX/cur', $mid, 10);
            SELECT last_insert_rowid();
            """;
        m.Parameters.AddWithValue("$mid", messageId);
        var msgId = (long)m.ExecuteScalar()!;

        using var a = conn.CreateCommand();
        a.CommandText = """
            INSERT INTO attachments (message_id, part_index, filename, content_type, size_bytes,
                                     extracted_text, extracted_at, extraction_status, ocr_model)
            VALUES ($msg, 0, 'scan.pdf', 'application/pdf', 100, 'text', $at, $status, $model);
            SELECT last_insert_rowid();
            """;
        a.Parameters.AddWithValue("$msg", msgId);
        a.Parameters.AddWithValue("$at", extractedAt);
        a.Parameters.AddWithValue("$status", status ?? AttachmentTextExtractor.StatusOcr);
        a.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
        return (long)a.ExecuteScalar()!;
    }

    private static string? ModelOf(TestServiceProvider ctx, long attachmentId)
    {
        using var conn = ctx.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ocr_model FROM attachments WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", attachmentId);
        return cmd.ExecuteScalar() as string;
    }
}
