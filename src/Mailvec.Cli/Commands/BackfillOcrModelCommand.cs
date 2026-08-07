using System.CommandLine;
using System.Globalization;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Vision;
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
/// <para><b>Scoped to statuses that unambiguously record an engine's verdict</b>
/// — <see cref="AttachmentOcrSql.BackfillableVerdict"/>, shared verbatim with
/// reocr's candidate query so the two cannot disagree about what a verdict is.
/// That means <c>'ocr'</c>, plus an IMAGE at <c>'no_text'</c>: the indexer can
/// never put an image there (<c>ResolveFormat</c> calls images Unsupported, and
/// <c>BuildResult</c>'s <c>no_text</c> only fires for document formats it
/// parsed), so <c>MarkAttachmentImageNoText</c> is its only writer.</para>
///
/// <para>Deliberately excluded: a PDF at <c>no_text</c>, which IS indexer output
/// for a scanned PDF it could not read natively, and <c>'failed'</c>, which also
/// covers corrupt <c>.eml</c> files and unopenable PDFs. Stamping either would
/// invent provenance for rows no engine ever touched.</para>
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
        // Shares AttachmentOcrSql.BackfillableVerdict with reocr's candidate
        // query. When these two disagreed, reocr counted an image at 'no_text'
        // as a completed verdict while this command refused to stamp one, so
        // `reocr --engine unknown` selected every image the backfill had
        // skipped. One string, so they cannot drift apart again.
        var verdict = AttachmentOcrSql.BackfillableVerdict;
        var inRange = $"{verdict} AND a.extracted_at IS NOT NULL AND datetime(a.extracted_at) < datetime($cutoff)";

        // OcrProvenance.PreProvider survives even --overwrite. It is not an
        // engine attribution to be corrected — it records that NO engine looked
        // at the document (unreadable bytes, or the image gate). Rewriting it to
        // an engine id would put gate-rejected banner images back in range of
        // `reocr --engine <that id>`, which would re-attempt them forever: the
        // exact failure PreProvider exists to prevent, reached through the other
        // door. --overwrite is for correcting a wrong ENGINE, and the command's
        // own rule is that a value the pass observed at write time wins —
        // 'pipeline' is such a value.
        var predicate = overwrite
            ? $"{inRange} AND (a.ocr_model IS NULL OR a.ocr_model <> $preProvider)"
            : $"{inRange} AND a.ocr_model IS NULL";

        var cutoffIso = cutoff.ToString("O");

        long candidates, alreadyStamped, afterCutoff;
        using (var q = conn.CreateCommand())
        {
            q.CommandText = $"""
                SELECT
                  (SELECT COUNT(*) FROM attachments a WHERE {predicate}),
                  (SELECT COUNT(*) FROM attachments a WHERE {verdict} AND a.ocr_model IS NOT NULL),
                  (SELECT COUNT(*) FROM attachments a WHERE {verdict}
                     AND a.ocr_model IS NULL
                     AND (a.extracted_at IS NULL OR datetime(a.extracted_at) >= datetime($cutoff)));
                """;
            q.Parameters.AddWithValue("$cutoff", cutoffIso);
            q.Parameters.AddWithValue("$preProvider", OcrProvenance.PreProvider);
            using var r = q.ExecuteReader();
            r.Read();
            candidates = r.GetInt64(0);
            alreadyStamped = r.GetInt64(1);
            afterCutoff = r.GetInt64(2);
        }

        @out.WriteLine($"Cutoff        : {cutoffIso} (rows strictly before this)");
        @out.WriteLine($"Engine id     : {model}");
        @out.WriteLine($"Would stamp   : {candidates} attachment(s) holding an OCR verdict ('ocr', or an image at 'no_text')");
        @out.WriteLine($"Already stamped: {alreadyStamped} (left alone{(overwrite ? " — but --overwrite is set, so in-range ones are rewritten, except '" + OcrProvenance.PreProvider + "'" : "")})");
        // Counts only the UNSTAMPED ones. Once the passes have been stamping for
        // a while most post-cutoff rows carry an observed value, and reporting
        // those as "left NULL, re-OCR these" would turn a dry run whose whole
        // job is to be checkable into a misleading one.
        @out.WriteLine($"After cutoff, still unstamped: {afterCutoff} — re-OCR these so the passes stamp them from observation");

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
            // UPDATE takes no alias in SQLite, so run it against the ids the
            // aliased predicate selects rather than retyping the predicate.
            upd.CommandText = $"""
                UPDATE attachments SET ocr_model = $model
                WHERE id IN (SELECT a.id FROM attachments a WHERE {predicate});
                """;
            upd.Parameters.AddWithValue("$model", model);
            upd.Parameters.AddWithValue("$cutoff", cutoffIso);
            upd.Parameters.AddWithValue("$preProvider", OcrProvenance.PreProvider);
            updated = upd.ExecuteNonQuery();
        }
        tx.Commit();

        @out.WriteLine($"\nStamped {updated} attachment(s) as '{model}'.");
        @out.WriteLine("Note: this is an assertion, not an observation. Rows OCR'd from now on are");
        @out.WriteLine("stamped by the passes themselves at write time.");
        return 0;
    }
}
