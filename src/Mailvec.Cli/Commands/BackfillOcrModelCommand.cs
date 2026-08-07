using System.CommandLine;
using System.Globalization;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Stamps <c>attachments.ocr_model</c> on rows that were OCR'd before the column
/// existed — the operator-asserted half of the v10 migration.
///
/// <para><b>Why this is a command and not part of the migration.</b> The
/// migration cannot know which engine produced a pre-v10 row. Which provider was
/// configured, and when it changed, lives in the deployment's environment
/// (<c>Vision__Provider</c> in compose), which the repository cannot see. A
/// migration that inferred a value from a date would be recording an assumption
/// about unversioned operator state as a fact — and a WRONG stamp is worse than
/// none, because "re-OCR everything engine X produced" would then silently skip
/// the real X rows and re-run the other engine's. So the migration leaves NULL
/// ("provenance unknown, predates tracking") and an operator who actually knows
/// their history asserts it here, explicitly, with a date they choose.</para>
///
/// <para><b>Scoped to <c>extraction_status='ocr'</c> only</b>, and that is a real
/// limitation rather than an oversight. The other two OCR verdicts are
/// indistinguishable by status from non-OCR ones: <c>no_text</c> is also what the
/// indexer writes for a scanned PDF it could not read natively, and
/// <c>failed</c> also covers corrupt <c>.eml</c> files and unopenable PDFs. Only
/// <c>ocr</c> is unambiguously the product of a vision engine, so only <c>ocr</c>
/// can be back-stamped without inventing provenance for rows no engine ever
/// touched. Rows the new code writes from now on are stamped at all three
/// verdicts.</para>
///
/// <para>Dry-run by default, matching <c>mailvec reocr</c>: the whole point is
/// that you check the count against what you expect before asserting anything.</para>
/// </summary>
internal static class BackfillOcrModelCommand
{
    public static Command Build()
    {
        var modelOpt = new Option<string>("--model")
        {
            Description = "Engine id to stamp, in the same 'provider:model' shape the OCR passes write (e.g. 'ollama:qwen2.5vl:7b', 'mistral:mistral-ocr-2505').",
            Required = true,
        };
        var beforeOpt = new Option<string>("--before")
        {
            Description = "Only stamp rows whose extracted_at is strictly BEFORE this instant (ISO 8601, e.g. '2026-08-06' or '2026-08-06T14:00:00Z'). This is your assertion about when the engine changed on THIS deployment.",
            Required = true,
        };
        var applyOpt = new Option<bool>("--apply")
        {
            Description = "Actually write the stamps. Without this the command reports what it would stamp and exits.",
        };
        var overwriteOpt = new Option<bool>("--overwrite")
        {
            Description = "Also re-stamp rows that already carry an ocr_model. Off by default: a value written by the OCR pass itself is a fact observed at write time, and this command's input is an assertion — the fact should win.",
        };

        var cmd = new Command(
            "backfill-ocr-model",
            "Stamp attachments.ocr_model on OCR'd rows that predate provenance tracking (operator-asserted).")
        { modelOpt, beforeOpt, applyOpt, overwriteOpt };

        cmd.SetAction(parse =>
        {
            using var sp = CliServices.Build();
            sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
            return Execute(
                sp, Console.Out,
                parse.GetValue(modelOpt)!,
                parse.GetValue(beforeOpt)!,
                parse.GetValue(applyOpt),
                parse.GetValue(overwriteOpt));
        });

        return cmd;
    }

    /// <summary>Testable seam — the CLI action builds the container, this does the work.</summary>
    internal static int Execute(
        IServiceProvider sp, TextWriter @out, string model, string before, bool apply, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            @out.WriteLine("--model must be a non-empty engine id.");
            return 2;
        }

        // Parse to a real instant and re-serialise round-trip, so the comparison
        // below is against a normalized value rather than whatever shape the
        // operator typed. date_sent/extracted_at hold mixed-offset ISO strings,
        // which is exactly why every date comparison in this codebase goes
        // through datetime() rather than comparing text.
        if (!DateTimeOffset.TryParse(before, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var cutoff))
        {
            @out.WriteLine($"--before '{before}' is not a valid ISO 8601 date or timestamp.");
            return 2;
        }

        var factory = sp.GetRequiredService<ConnectionFactory>();
        using var conn = factory.Open();

        // datetime() on both sides: extracted_at is DateTimeOffset.ToString("O"),
        // so one column holds mixed offsets and a raw string compare puts
        // '...07:13:20-05:00' (12:13Z) below '...11:00:00+00:00' (11:00Z) —
        // exactly inverted. Same rule the date filters and reocr's ordering follow.
        var predicate = overwrite
            ? "extraction_status = $ocr AND extracted_at IS NOT NULL AND datetime(extracted_at) < datetime($cutoff)"
            : "extraction_status = $ocr AND extracted_at IS NOT NULL AND datetime(extracted_at) < datetime($cutoff) AND ocr_model IS NULL";

        var cutoffIso = cutoff.ToString("O");

        long candidates, alreadyStamped, afterCutoff;
        using (var q = conn.CreateCommand())
        {
            q.CommandText = $"""
                SELECT
                  (SELECT COUNT(*) FROM attachments WHERE {predicate}),
                  (SELECT COUNT(*) FROM attachments WHERE extraction_status = $ocr AND ocr_model IS NOT NULL),
                  (SELECT COUNT(*) FROM attachments WHERE extraction_status = $ocr
                     AND (extracted_at IS NULL OR datetime(extracted_at) >= datetime($cutoff)));
                """;
            q.Parameters.AddWithValue("$ocr", AttachmentTextExtractor.StatusOcr);
            q.Parameters.AddWithValue("$cutoff", cutoffIso);
            using var r = q.ExecuteReader();
            r.Read();
            candidates = r.GetInt64(0);
            alreadyStamped = r.GetInt64(1);
            afterCutoff = r.GetInt64(2);
        }

        @out.WriteLine($"Cutoff        : {cutoffIso} (rows strictly before this)");
        @out.WriteLine($"Engine id     : {model}");
        @out.WriteLine($"Would stamp   : {candidates} attachment(s) at status 'ocr'");
        @out.WriteLine($"Already stamped: {alreadyStamped} (left alone{(overwrite ? " — but --overwrite is set, so in-range ones WILL be rewritten" : "")})");
        @out.WriteLine($"At/after cutoff: {afterCutoff} left NULL — re-OCR these to have the passes stamp them for real");

        if (candidates == 0)
        {
            @out.WriteLine("\nNothing to do.");
            return 0;
        }

        if (!apply)
        {
            @out.WriteLine("\nDry run — nothing written. Re-run with --apply once the counts match what you expect.");
            return 0;
        }

        // One transaction: this is a single logical assertion over a bounded set,
        // not a long-running per-message pass, so it does not hit the writer-lock
        // problem that forces reocr/extract-attachments to commit per message.
        using var tx = conn.BeginTransaction();
        int updated;
        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = $"UPDATE attachments SET ocr_model = $model WHERE {predicate};";
            upd.Parameters.AddWithValue("$model", model);
            upd.Parameters.AddWithValue("$ocr", AttachmentTextExtractor.StatusOcr);
            upd.Parameters.AddWithValue("$cutoff", cutoffIso);
            updated = upd.ExecuteNonQuery();
        }
        tx.Commit();

        @out.WriteLine($"\nStamped {updated} attachment(s) as '{model}'.");
        @out.WriteLine("Note: this is an assertion, not an observation. Rows OCR'd from now on are");
        @out.WriteLine("stamped by the passes themselves at write time.");
        return 0;
    }
}
