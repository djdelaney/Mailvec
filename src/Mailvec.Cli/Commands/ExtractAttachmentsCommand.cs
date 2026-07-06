using System.CommandLine;
using System.Globalization;
using Mailvec.Core;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Backfills attachment-text extraction for v3-era rows (and any rows the
/// indexer skipped, e.g. an early-Phase-4.5 build that ran before
/// AttachmentTextExtractor was wired in). Walks every attachment whose
/// <c>extraction_status</c> is NULL, opens the parent <c>.eml</c> from
/// Maildir, runs <see cref="AttachmentTextExtractor"/>, and writes back
/// <c>extracted_text</c> / <c>extracted_at</c> / <c>extraction_status</c>.
///
/// After per-message updates, refreshes the denormalized
/// <c>messages.attachment_text</c> column (which feeds messages_fts via the
/// AU trigger) and — unless <c>--no-reembed</c> is passed — clears the
/// message's chunks/embedded_at so the embedder picks it up and re-generates
/// the vector representation with attachment chunks included.
///
/// Why this exists: the v3 -> v4 migration adds the columns but cannot
/// backfill <c>extracted_text</c>, because re-extraction needs the .eml
/// bytes, and the indexer's mtime fast-path skips files whose mtime hasn't
/// changed (which it hasn't — mbsync didn't touch them). Before this command,
/// the only supported v3->v4 path was "drop the DB and re-ingest", which is
/// increasingly painful as archive size grows. With it, in-place upgrade is
/// a one-time CLI run.
///
/// Idempotent: only touches attachments whose <c>extraction_status</c> is
/// still NULL. Safe to ^C and resume — uncommitted per-message transactions
/// just roll back, the next run picks up where we left off.
///
/// <para>The <c>--reextract-*</c> flags (<c>calendar</c> / <c>vcard</c> /
/// <c>office</c> / <c>text</c>) switch the candidate predicate from "status IS
/// NULL" to "looks like that format (by content-type or extension), regardless
/// of current status". These are the one-time backfills for the routing/format
/// additions: rows previously stamped <c>unsupported</c> (e.g. octet-stream
/// .ics / .vcf, or any .xlsx/.pptx) get recovered, and rows already stamped
/// through an old raw path get re-extracted through the new handler
/// (<c>--reextract-text</c> is the backfill for the declared-charset decode
/// fix — rows whose non-Latin text was mojibake'd through the old UTF-8→1252
/// ladder and stamped 'done'). The coarse SQL gate is intentionally
/// over-inclusive — <see cref="AttachmentTextExtractor"/> remains the authority
/// on the actual format, so a false-positive row just re-stamps to whatever it
/// was.</para>
/// </summary>
internal static class ExtractAttachmentsCommand
{
    public static Command Build()
    {
        var limitOpt = new Option<int?>("--limit") { Description = "Cap the number of messages processed this run (useful for a dry-run on a slice before committing to the full backfill)." };
        var batchOpt = new Option<int>("--batch") { Description = "Messages fetched per DB roundtrip. Default 100.", DefaultValueFactory = _ => 100 };
        var noReembedOpt = new Option<bool>("--no-reembed") { Description = "Skip clearing chunks/embedded_at for messages that gained text. Faster, but the vector index won't reflect attachment content until you run the embedder against those messages explicitly (e.g. via `mailvec reindex --all`)." };
        var reextractCalendarOpt = new Option<bool>("--reextract-calendar") { Description = "Re-extract calendar (.ics / text-calendar / application-ics) attachments regardless of current status, applying the unfold + field-extraction pass. One-time backfill for the calendar-MIME routing fix." };
        var reextractVCardOpt = new Option<bool>("--reextract-vcard") { Description = "Re-extract vCard (.vcf / text-vcard) attachments regardless of current status. One-time backfill for the vCard-MIME routing fix. Mutually exclusive with the other --reextract-* flags; run one at a time." };
        var reextractOfficeOpt = new Option<bool>("--reextract-office") { Description = "Re-extract Office Open XML spreadsheets (.xlsx) and presentations (.pptx) regardless of current status. One-time backfill for the xlsx/pptx extractor. Mutually exclusive with the other --reextract-* flags; run one at a time." };
        var reextractTextOpt = new Option<bool>("--reextract-text") { Description = "Re-extract text-shaped attachments (text/* content types, .txt/.md/.csv/.log) regardless of current status. One-time backfill for the declared-charset decode fix — recovers non-Latin (ISO-2022-JP, Shift-JIS, GB2312, …) text that the old UTF-8→Windows-1252 ladder indexed as mojibake. Mutually exclusive with the other --reextract-* flags; run one at a time." };

        var cmd = new Command("extract-attachments", "Backfill attachment-text extraction for messages where the indexer never ran the extractor (v3->v4 upgrade path or pre-Phase-4.5 ingest).")
        {
            limitOpt,
            batchOpt,
            noReembedOpt,
            reextractCalendarOpt,
            reextractVCardOpt,
            reextractOfficeOpt,
            reextractTextOpt,
        };

        cmd.SetAction(parse => Run(
            limit: parse.GetValue(limitOpt),
            batch: Math.Max(1, parse.GetValue(batchOpt)),
            noReembed: parse.GetValue(noReembedOpt),
            // First flag set wins if more than one is (mistakenly) passed.
            reextractKind: parse.GetValue(reextractCalendarOpt) ? "calendar"
                : parse.GetValue(reextractVCardOpt) ? "vcard"
                : parse.GetValue(reextractOfficeOpt) ? "office"
                : parse.GetValue(reextractTextOpt) ? "text"
                : null));
        return cmd;
    }

    private static int Run(int? limit, int batch, bool noReembed, string? reextractKind)
    {
        using var sp = CliServices.Build();
        return Execute(sp, limit, batch, noReembed, reextractKind, Console.Out, Console.Error);
    }

    /// <summary>
    /// Coarse SQL gate for which attachment rows are re-extraction candidates.
    /// Default (<paramref name="reextractKind"/> null) targets never-extracted
    /// rows (status IS NULL); a re-extract kind ("calendar" / "vcard") targets
    /// anything that looks like that format regardless of status, so the routing
    /// fixes reach already-stamped rows. <paramref name="col"/> is the table
    /// alias/name to qualify the columns with (e.g. "a" or "attachments").
    /// </summary>
    private static string CandidatePredicate(string? reextractKind, string col) => reextractKind switch
    {
        "calendar" => $"""
            (
                lower({col}.content_type) IN ('text/calendar', 'application/ics', 'application/calendar', 'text/x-vcalendar')
                OR lower({col}.filename) LIKE '%.ics'
                OR lower({col}.filename) LIKE '%.ical'
                OR lower({col}.filename) LIKE '%.vcs'
            )
            """,
        "vcard" => $"""
            (
                lower({col}.content_type) IN ('text/vcard', 'text/x-vcard', 'application/vcard')
                OR lower({col}.filename) LIKE '%.vcf'
                OR lower({col}.filename) LIKE '%.vcard'
            )
            """,
        "office" => $"""
            (
                lower({col}.content_type) IN (
                    'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
                    'application/vnd.openxmlformats-officedocument.presentationml.presentation')
                OR lower({col}.filename) LIKE '%.xlsx'
                OR lower({col}.filename) LIKE '%.pptx'
            )
            """,
        // Over-inclusive on purpose: text/% also catches text/calendar and
        // text/vcard, which just re-route through their own handlers and
        // re-stamp identically. The extractor is the authority.
        "text" => $"""
            (
                lower({col}.content_type) LIKE 'text/%'
                OR lower({col}.filename) LIKE '%.txt'
                OR lower({col}.filename) LIKE '%.md'
                OR lower({col}.filename) LIKE '%.csv'
                OR lower({col}.filename) LIKE '%.log'
            )
            """,
        _ => $"{col}.extraction_status IS NULL",
    };

    /// <summary>Test seam — see <see cref="PurgeDeletedCommand"/> for the pattern.</summary>
    internal static int Execute(IServiceProvider sp, int? limit, int batch, bool noReembed, string? reextractKind, TextWriter @out, TextWriter err)
    {
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();

        var ingestOptions = sp.GetRequiredService<IOptions<IngestOptions>>().Value;
        var maildirRoot = PathExpansion.Expand(ingestOptions.MaildirRoot);
        if (string.IsNullOrEmpty(maildirRoot) || !Directory.Exists(maildirRoot))
        {
            err.WriteLine($"Maildir root not found: '{maildirRoot}'. Set Ingest__MaildirRoot or fix appsettings.json.");
            return 2;
        }

        var extractor = sp.GetRequiredService<AttachmentTextExtractor>();
        var chunks = sp.GetRequiredService<ChunkRepository>();
        var connections = sp.GetRequiredService<ConnectionFactory>();

        // Pre-count so the progress line means something. Cheap — single
        // index scan on (extraction_status IS NULL).
        var candidatePredicate = CandidatePredicate(reextractKind, "a");
        long totalCandidates;
        long affectedMessages;
        using (var conn = connections.Open())
        using (var count = conn.CreateCommand())
        {
            count.CommandText = $"""
                SELECT COUNT(*)
                FROM attachments a
                JOIN messages m ON m.id = a.message_id
                WHERE {candidatePredicate}
                  AND m.deleted_at IS NULL;
                """;
            totalCandidates = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);

            count.CommandText = $"""
                SELECT COUNT(DISTINCT a.message_id)
                FROM attachments a
                JOIN messages m ON m.id = a.message_id
                WHERE {candidatePredicate}
                  AND m.deleted_at IS NULL;
                """;
            affectedMessages = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (totalCandidates == 0)
        {
            @out.WriteLine("No attachments need extraction. Nothing to do.");
            return 0;
        }

        var ceiling = limit is { } l ? Math.Min(l, affectedMessages) : affectedMessages;
        @out.WriteLine($"Backfill candidates: {totalCandidates:N0} attachments across {affectedMessages:N0} messages.");
        if (limit is not null) @out.WriteLine($"Limit: this run will process at most {ceiling:N0} message(s).");
        @out.WriteLine($"Re-embed after backfill: {(noReembed ? "no (use --reembed or `mailvec reindex --all` later)" : "yes")}.");
        @out.WriteLine();

        long messagesProcessed = 0;
        long messagesWithNewText = 0;
        long attachmentsExtracted = 0;
        var statusCounts = new Dictionary<string, long>(StringComparer.Ordinal);

        // Cursor pagination by messages.id (rowid-ordered). We don't OFFSET
        // because the WHERE filter changes as we update rows — OFFSET 100
        // after processing 100 rows would skip the next 100, since the
        // already-processed rows no longer match the filter. A monotonically
        // increasing cursor on m.id sidesteps this entirely.
        long cursor = 0;
        while (limit is null || messagesProcessed < limit)
        {
            var pageSize = limit is { } pl ? Math.Min(batch, pl - (int)messagesProcessed) : batch;
            var candidates = LoadMessagePage(connections, cursor, pageSize, reextractKind);
            if (candidates.Count == 0) break;

            foreach (var msg in candidates)
            {
                cursor = msg.Id;
                messagesProcessed++;

                var maildirFile = Path.Combine(maildirRoot, msg.MaildirPath.Replace('/', Path.DirectorySeparatorChar), msg.MaildirFilename);
                if (!File.Exists(maildirFile))
                {
                    // Stamp every candidate attachment 'failed' so we don't
                    // loop on this message forever. Most likely cause:
                    // mbsync moved/renamed the file and the indexer hasn't
                    // rescanned yet. The user can re-run after a rescan.
                    err.WriteLine($"  msg {msg.Id}: source not found ({maildirFile}); marking attachments 'failed'.");
                    StampMessageAttachmentsFailed(connections, msg.Id);
                    continue;
                }

                if (TryProcessMessage(connections, extractor, msg, maildirFile, reextractKind, statusCounts, err, out var newTextCount, out var attachmentsThisMessage))
                {
                    attachmentsExtracted += attachmentsThisMessage;
                    if (newTextCount > 0)
                    {
                        messagesWithNewText++;
                        if (!noReembed)
                        {
                            // ClearEmbeddingsForMessage opens its own
                            // connection + transaction. Cheap (a single
                            // message's worth of chunks) and isolates the
                            // failure mode — if the embedder is mid-write
                            // and we collide, we just retry on the next pass.
                            chunks.ClearEmbeddingsForMessage(msg.Id);
                        }
                    }
                }

                if (messagesProcessed % 50 == 0)
                {
                    @out.WriteLine($"  ... {messagesProcessed:N0}/{ceiling:N0} messages, {attachmentsExtracted:N0} attachments stamped");
                }
            }
        }

        @out.WriteLine();
        @out.WriteLine($"Processed {messagesProcessed:N0} message(s); stamped {attachmentsExtracted:N0} attachment(s).");
        if (statusCounts.Count > 0)
        {
            @out.WriteLine("Status breakdown:");
            foreach (var (status, n) in statusCounts.OrderByDescending(kv => kv.Value))
            {
                @out.WriteLine($"  {status,-12} {n:N0}");
            }
        }
        if (!noReembed && messagesWithNewText > 0)
        {
            @out.WriteLine();
            @out.WriteLine($"Cleared chunks/embedded_at for {messagesWithNewText:N0} message(s). The embedder picks up cleared messages on its next poll — no need to run `reindex` separately.");
        }
        return 0;
    }

    /// <summary>
    /// Process one message: open the .eml, walk its MIME attachments, run
    /// the extractor against each one whose DB row still has NULL status,
    /// persist the result inside a single transaction. Refreshes
    /// <c>messages.attachment_text</c> at the end so the FTS5 trigger picks
    /// up newly-extracted text.
    /// </summary>
    private static bool TryProcessMessage(
        ConnectionFactory connections,
        AttachmentTextExtractor extractor,
        MessageRow msg,
        string maildirFile,
        string? reextractKind,
        Dictionary<string, long> statusCounts,
        TextWriter err,
        out int newTextCount,
        out int attachmentsThisMessage)
    {
        newTextCount = 0;
        attachmentsThisMessage = 0;

        // Open and parse outside the transaction — MimeKit can be slow on
        // large attachments and we don't want to hold a write lock across
        // PdfPig parsing. Per-message transaction is opened later for the
        // batch of UPDATEs.
        MimeMessage mime;
        try
        {
            using var stream = File.OpenRead(maildirFile);
            mime = MimeMessage.Load(stream);
        }
        catch (Exception ex)
        {
            err.WriteLine($"  msg {msg.Id}: parse failed ({ex.GetType().Name}: {ex.Message}); skipping.");
            return false;
        }

        // MessageParts.Indexable — not mime.Attachments — per the part_index
        // invariant: the writer (MessageParser) enumerates attachments first,
        // then appends inline images, so an inline-image row's part_index only
        // resolves through the same enumeration. With mime.Attachments alone,
        // any candidate row pointing at an inline part would be mis-stamped
        // 'failed' as "part doesn't exist".
        var entitiesByPart = MessageParts.Indexable(mime)
            .Select((entity, index) => (PartIndex: index, Entity: entity))
            .ToDictionary(x => x.PartIndex, x => x.Entity);

        using var conn = connections.Open();
        using var tx = conn.BeginTransaction();

        var candidates = LoadCandidatesForMessage(conn, tx, msg.Id, reextractKind);
        if (candidates.Count == 0)
        {
            // Race or dup-call: another extract pass already stamped these.
            tx.Commit();
            return true;
        }

        var stamp = DateTimeOffset.UtcNow.ToString("O");
        foreach (var att in candidates)
        {
            string status;
            string? text;

            if (!entitiesByPart.TryGetValue(att.PartIndex, out var entity))
            {
                // The DB row claims a partIndex that doesn't exist in the
                // current MIME parse. Could happen if the .eml was rewritten
                // post-ingest. Stamp 'failed' so we don't retry forever.
                status = AttachmentTextExtractor.StatusFailed;
                text = null;
            }
            else
            {
                var result = extractor.Extract(entity, att.FileName, att.ContentType, att.SizeBytes);
                status = result.Status;
                text = result.Text;
            }

            UpdateAttachment(conn, tx, att.Id, text, stamp, status);
            attachmentsThisMessage++;
            statusCounts.TryGetValue(status, out var prior);
            statusCounts[status] = prior + 1;
            if (!string.IsNullOrEmpty(text)) newTextCount++;
        }

        // Refresh messages.attachment_text from the (now-updated) attachments
        // rows. Mirrors MessageRepository.BuildAttachmentText / migration 005's
        // backfill pattern. The UPDATE fires messages_au, which deletes +
        // re-inserts the FTS5 row with the new attachment_text content.
        using (var refresh = conn.CreateCommand())
        {
            refresh.Transaction = tx;
            refresh.CommandText = """
                UPDATE messages
                SET attachment_text = (
                    SELECT group_concat(extracted_text, ' ')
                    FROM attachments
                    WHERE attachments.message_id = messages.id
                      AND attachments.extracted_text IS NOT NULL
                      AND length(attachments.extracted_text) > 0
                )
                WHERE id = $mid;
                """;
            refresh.Parameters.AddWithValue("$mid", msg.Id);
            refresh.ExecuteNonQuery();
        }

        tx.Commit();
        return true;
    }

    private static IReadOnlyList<MessageRow> LoadMessagePage(ConnectionFactory connections, long cursor, int pageSize, string? reextractKind)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT m.id, m.maildir_path, m.maildir_filename
            FROM messages m
            WHERE m.deleted_at IS NULL
              AND m.id > $cursor
              AND EXISTS (
                  SELECT 1 FROM attachments a
                  WHERE a.message_id = m.id AND {CandidatePredicate(reextractKind, "a")}
              )
            ORDER BY m.id
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$cursor", cursor);
        cmd.Parameters.AddWithValue("$limit", pageSize);

        var list = new List<MessageRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MessageRow(
                Id: reader.GetInt64(0),
                MaildirPath: reader.GetString(1),
                MaildirFilename: reader.GetString(2)));
        }
        return list;
    }

    private static List<AttachmentCandidate> LoadCandidatesForMessage(SqliteConnection conn, SqliteTransaction tx, long messageId, string? reextractKind)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT id, part_index, filename, content_type, size_bytes
            FROM attachments
            WHERE message_id = $mid AND {CandidatePredicate(reextractKind, "attachments")}
            ORDER BY part_index;
            """;
        cmd.Parameters.AddWithValue("$mid", messageId);

        var list = new List<AttachmentCandidate>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AttachmentCandidate(
                Id: reader.GetInt64(0),
                PartIndex: reader.GetInt32(1),
                FileName: reader.IsDBNull(2) ? null : reader.GetString(2),
                ContentType: reader.IsDBNull(3) ? null : reader.GetString(3),
                SizeBytes: reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }
        return list;
    }

    private static void UpdateAttachment(SqliteConnection conn, SqliteTransaction tx, long attachmentId, string? text, string stamp, string status)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE attachments
            SET extracted_text = $text,
                extracted_at = $at,
                extraction_status = $status
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", stamp);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$id", attachmentId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Stamp every NULL-status attachment on a message as 'failed', for the
    /// case where the source .eml is missing. Lets the next run skip the
    /// message instead of re-attempting against a still-missing file.
    /// </summary>
    private static void StampMessageAttachmentsFailed(ConnectionFactory connections, long messageId)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE attachments
            SET extraction_status = $status, extracted_at = $at
            WHERE message_id = $mid AND extraction_status IS NULL;
            """;
        cmd.Parameters.AddWithValue("$status", AttachmentTextExtractor.StatusFailed);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$mid", messageId);
        cmd.ExecuteNonQuery();
    }

    private sealed record MessageRow(long Id, string MaildirPath, string MaildirFilename);
    private sealed record AttachmentCandidate(long Id, int PartIndex, string? FileName, string? ContentType, long? SizeBytes);
}
