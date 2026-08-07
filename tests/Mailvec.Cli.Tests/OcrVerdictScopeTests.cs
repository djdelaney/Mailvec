using Mailvec.Cli.Commands;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Vision;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Tests;

/// <summary>
/// <c>reocr</c> and <c>backfill-ocr-model</c> must agree on what counts as an
/// OCR verdict. They didn't once, and the gap was invisible until it was run
/// against a real corpus: reocr treated an image at <c>no_text</c> as a
/// completed verdict while the backfill refused to stamp one, so
/// <c>reocr --engine unknown</c> selected 1,380 images the backfill had left
/// NULL — every one of which would have been re-sent to the hosted provider.
///
/// <para>Sharing <see cref="AttachmentOcrSql.BackfillableVerdict"/> fixed the
/// instance. This file is what makes it an invariant: re-inline the predicate in
/// either command and these fail, rather than the drift surviving to the next
/// person who runs a dry run and believes the count.</para>
/// </summary>
public class OcrVerdictScopeTests
{
    private const string Engine = "ollama:qwen2.5vl:7b";

    /// <summary>
    /// Far enough ahead that every staged row is in range, so the test is about
    /// scope alone and never accidentally about the cutoff.
    /// </summary>
    private const string FarFuture = "2099-01-01";

    [Fact]
    public void Everything_reocr_can_see_with_unknown_provenance_is_something_the_backfill_stamps()
    {
        // The invariant, stated as a round trip: stamp everything the backfill
        // considers a verdict, and reocr must then have no unknown-provenance
        // work left. Any row reocr can still see is one the backfill refused to
        // claim — which is exactly the shape of the original bug.
        using var ctx = new TestServiceProvider();
        Stage(ctx, "ocr@x", AttachmentTextExtractor.StatusOcr, "application/pdf", "scan.pdf");
        Stage(ctx, "img@x", AttachmentTextExtractor.StatusNoText, "image/jpeg", "photo.jpg");
        Stage(ctx, "octet@x", AttachmentTextExtractor.StatusNoText, "application/octet-stream", "IMG_1234.jpeg");
        Stage(ctx, "pdf@x", AttachmentTextExtractor.StatusNoText, "application/pdf", "scan.pdf");
        Stage(ctx, "done@x", AttachmentTextExtractor.StatusDone, "application/pdf", "doc.pdf");
        Stage(ctx, "unsup@x", AttachmentTextExtractor.StatusUnsupported, "image/png", "pending.png");

        BackfillOcrModelCommand.Execute(ctx.Services, new StringWriter(), Engine, FarFuture,
            apply: true, overwrite: false).ShouldBe(0);

        var writer = new StringWriter();
        ReocrCommand.Execute(ctx.Services, writer, apply: false, includeFailed: false, limit: 0,
            engine: OcrEngineFilter.Unknown).ShouldBe(0);

        // A row reocr still sees here is one the backfill declined to stamp —
        // i.e. the two predicates have drifted apart again.
        writer.ToString().ShouldContain("Nothing to re-OCR");
    }

    [Fact]
    public void The_backfill_claims_no_row_reocr_would_not_have_selected()
    {
        // The converse direction. Over-claiming is the worse failure: it invents
        // provenance for rows no engine ever touched, and unlike under-claiming
        // it cannot be noticed by running anything afterwards.
        using var ctx = new TestServiceProvider();
        var pdfNoText = Stage(ctx, "pdf@x", AttachmentTextExtractor.StatusNoText, "application/pdf", "scan.pdf");
        var native = Stage(ctx, "done@x", AttachmentTextExtractor.StatusDone, "application/pdf", "doc.pdf");
        var pending = Stage(ctx, "unsup@x", AttachmentTextExtractor.StatusUnsupported, "image/png", "pending.png");
        var gif = Stage(ctx, "gif@x", AttachmentTextExtractor.StatusNoText, "image/gif", "banner.gif");

        BackfillOcrModelCommand.Execute(ctx.Services, new StringWriter(), Engine, FarFuture,
            apply: true, overwrite: false).ShouldBe(0);

        // A PDF at no_text is indexer output; 'done' is native extraction;
        // 'unsupported' is a PENDING image the pass hasn't reached; GIF is
        // excluded from the image pass entirely. None is an engine verdict.
        ModelOf(ctx, pdfNoText).ShouldBeNull();
        ModelOf(ctx, native).ShouldBeNull();
        ModelOf(ctx, pending).ShouldBeNull();
        ModelOf(ctx, gif).ShouldBeNull();
    }

    [Fact]
    public void Failed_is_out_of_scope_for_the_backfill_by_design_not_by_drift()
    {
        // reocr --include-failed CAN select 'failed' rows that the backfill will
        // never stamp. That asymmetry is deliberate, not a regression of the bug
        // above: 'failed' also covers corrupt .eml files and unopenable PDFs, so
        // it is not evidence any engine looked. Pinned so nobody "fixes" the
        // backfill to cover it in the name of symmetry.
        using var ctx = new TestServiceProvider();
        var failed = Stage(ctx, "failed@x", AttachmentTextExtractor.StatusFailed, "application/pdf", "broken.pdf");

        BackfillOcrModelCommand.Execute(ctx.Services, new StringWriter(), Engine, FarFuture,
            apply: true, overwrite: false).ShouldBe(0);

        ModelOf(ctx, failed).ShouldBeNull("'failed' is ambiguous — never back-stamp it");
    }

    [Fact]
    public void Overwrite_corrects_a_wrong_engine_but_never_a_pre_provider_verdict()
    {
        // 'pipeline' is not an engine attribution to correct — it records that NO
        // engine looked (unreadable bytes, or the image dimension gate). Rewriting
        // it to an engine id would put gate-rejected images back in range of
        // `reocr --engine <that id>`, which re-attempts them forever: the exact
        // failure OcrProvenance.PreProvider exists to prevent, through the other
        // door. --overwrite is for a wrong engine, and the command's own rule is
        // that a value the pass observed at write time wins.
        using var ctx = new TestServiceProvider();
        var wrongEngine = Stage(ctx, "wrong@x", AttachmentTextExtractor.StatusOcr, "application/pdf", "scan.pdf",
            model: "mistral:mistral-ocr-4-0");
        var gated = Stage(ctx, "gated@x", AttachmentTextExtractor.StatusNoText, "image/jpeg", "banner.jpg",
            model: OcrProvenance.PreProvider);

        BackfillOcrModelCommand.Execute(ctx.Services, new StringWriter(), Engine, FarFuture,
            apply: true, overwrite: true).ShouldBe(0);

        ModelOf(ctx, wrongEngine).ShouldBe(Engine, "--overwrite is for correcting an engine attribution");
        ModelOf(ctx, gated).ShouldBe(OcrProvenance.PreProvider, "no engine ran; that is not an attribution to correct");
    }

    // ---- helpers ----

    private static long Stage(
        TestServiceProvider ctx, string messageId, string status,
        string contentType, string filename, string? model = null)
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
            VALUES ($msg, 0, $fn, $ct, 100, 'text', '2026-08-01T10:00:00Z', $status, $model);
            SELECT last_insert_rowid();
            """;
        a.Parameters.AddWithValue("$msg", msgId);
        a.Parameters.AddWithValue("$fn", filename);
        a.Parameters.AddWithValue("$ct", contentType);
        a.Parameters.AddWithValue("$status", status);
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
