using System.CommandLine;
using Mailvec.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Re-queue attachments holding a previous OCR engine's verdict, so the
/// currently-configured vision provider reprocesses them.
///
/// Why this has to exist: swapping <c>Vision:Provider</c> re-OCRs nothing. The
/// PDF pass selects <c>extraction_status='no_text'</c> and the image pass
/// <c>'unsupported'</c>, so every document the old engine finished — 'ocr' (it
/// produced text) or an image at 'no_text' (it decided there was none) — is
/// matched by neither query and keeps that engine's output forever, mistakes
/// included. A hallucinated transcription stays indexed and searchable with
/// nothing left to re-trigger it.
///
/// <para><b>Defaults to a dry run.</b> Resetting clears the stored text
/// immediately, while the replacement arrives only as the embedder's OCR passes
/// drain the backlog — so a reset against a misconfigured or unreachable
/// provider leaves those documents unsearchable for as long as it stays broken.
/// Printing the plan first and requiring <c>--apply</c> keeps that from being
/// one keystroke away.</para>
///
/// <para>Safe to interrupt: each message commits on its own, and a committed
/// message is already selectable by the OCR passes.</para>
/// </summary>
internal static class ReocrCommand
{
    public static Command Build()
    {
        var applyOpt = new Option<bool>("--apply")
        {
            Description = "Actually perform the reset. Without this the command prints what it would do and exits.",
        };
        var includeFailedOpt = new Option<bool>("--include-failed")
        {
            Description = "Also reset attachments stamped 'failed'. Opt-in because 'failed' also covers non-OCR extraction failures (corrupt .eml, unopenable PDF) that a new vision provider won't fix.",
        };
        var limitOpt = new Option<int>("--limit")
        {
            Description = "Cap the number of attachments reset this run, for working through a large backlog in slices. 0 (default) means no cap.",
            DefaultValueFactory = _ => 0,
        };
        var engineOpt = new Option<string?>("--engine")
        {
            Description = "Only reset verdicts produced by this engine id (as recorded in attachments.ocr_model, e.g. 'ollama:qwen2.5vl:7b'), or 'unknown' for rows with no recorded provenance (pre-v10 output). Omit to reset every eligible verdict. This is what makes a provider switch re-run only the old engine's work instead of the whole corpus.",
        };

        var orderOpt = new Option<string>("--order")
        {
            Description = "Which end of the mailbox to work from: 'newest' (default) or 'oldest'. Ordered by the message's DATE, not by insertion order — so --limit covers the most recent scans rather than whatever the initial ingest inserted first.",
            DefaultValueFactory = _ => "newest",
        };
        orderOpt.Validators.Add(r =>
        {
            var v = r.GetValue(orderOpt);
            if (!string.Equals(v, "newest", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(v, "oldest", StringComparison.OrdinalIgnoreCase))
            {
                r.AddError($"--order must be 'newest' or 'oldest', got '{v}'.");
            }
        });

        var cmd = new Command(
            "reocr",
            "Re-queue attachments already OCR'd (or ruled textless) by a previous vision provider, so the configured one reprocesses them.")
        {
            applyOpt, includeFailedOpt, limitOpt, orderOpt, engineOpt,
        };

        cmd.SetAction(parse => Run(
            apply: parse.GetValue(applyOpt),
            includeFailed: parse.GetValue(includeFailedOpt),
            limit: parse.GetValue(limitOpt),
            order: string.Equals(parse.GetValue(orderOpt), "oldest", StringComparison.OrdinalIgnoreCase)
                ? OcrResetOrder.Oldest
                : OcrResetOrder.Newest,
            engine: parse.GetValue(engineOpt)));
        return cmd;
    }

    private static int Run(bool apply, bool includeFailed, int limit, OcrResetOrder order, string? engine)
    {
        using var sp = CliServices.Build();
        return Execute(sp, Console.Out, apply, includeFailed, limit, order, engine);
    }

    /// <summary>Test seam — see <see cref="PurgeDeletedCommand"/> for the pattern.</summary>
    internal static int Execute(
        IServiceProvider sp, TextWriter @out, bool apply, bool includeFailed, int limit,
        OcrResetOrder order = OcrResetOrder.Newest, string? engine = null)
    {
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        var messages = sp.GetRequiredService<MessageRepository>();

        var candidates = messages.EnumerateOcrResetCandidates(includeFailed, limit, order, engine);
        if (candidates.Count == 0)
        {
            @out.WriteLine(engine is null
                ? "Nothing to re-OCR: no attachments are holding a previous engine's verdict."
                : $"Nothing to re-OCR matching --engine {engine}.");
            return 0;
        }

        var messageCount = candidates.Select(c => c.MessageId).Distinct().Count();
        // State the order explicitly. With --limit it decides WHICH documents
        // get done, so it must not be something the operator has to infer.
        var orderLabel = order == OcrResetOrder.Newest ? "newest mail first" : "oldest mail first";
        @out.WriteLine($"{candidates.Count} attachment(s) across {messageCount} message(s) would be re-OCR'd ({orderLabel}):");
        foreach (var g in candidates.GroupBy(c => c.CurrentStatus).OrderByDescending(g => g.Count()))
        {
            var targets = string.Join(", ", g.Select(c => c.TargetStatus).Distinct().OrderBy(t => t));
            @out.WriteLine($"  {g.Key,-12} -> {targets,-12} {g.Count(),6}");
        }
        @out.WriteLine();

        if (!apply)
        {
            @out.WriteLine("Dry run — nothing changed. Re-run with --apply to commit.");
            @out.WriteLine(
                "Note: --apply clears the stored OCR text immediately; replacement text appears only as the " +
                "embedder's OCR passes drain the backlog. Confirm the provider works first (`mailvec doctor`).");
            return 0;
        }

        int resetAttachments = 0, resetMessages = 0, skipped = 0;

        // One transaction per message, not one for the whole run. Each message's
        // clear + attachment_text rebuild + re-queue must be atomic together
        // (MessageRepository.ResetOcrForMessage documents why), but holding
        // SQLite's single writer slot across thousands of messages would stall
        // the indexer, the embedder and the OCR write-back for the duration —
        // the same trap extract-attachments was fixed for.
        foreach (var group in candidates.GroupBy(c => c.MessageId))
        {
            var cleared = messages.ResetOcrForMessage(group.Key, [.. group]);
            if (cleared == 0)
            {
                // The OCR pass re-decided these rows between enumeration and the
                // write. Its verdict is newer than our snapshot, so keep it.
                skipped += group.Count();
                continue;
            }
            resetAttachments += cleared;
            resetMessages++;
        }

        @out.WriteLine($"Reset {resetAttachments} attachment(s) across {resetMessages} message(s).");
        if (skipped > 0)
            @out.WriteLine($"Skipped {skipped} attachment(s) whose status changed while this ran (their newer state was kept).");
        @out.WriteLine("They will be re-OCR'd by the embedder and re-embedded. Watch progress with `mailvec status`.");
        return 0;
    }
}
