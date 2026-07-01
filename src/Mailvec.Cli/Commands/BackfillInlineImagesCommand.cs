using System.CommandLine;
using System.Globalization;
using Mailvec.Core;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailvec.Cli.Commands;

/// <summary>
/// Backfills attachment rows for inline (<c>Content-Disposition: inline</c> /
/// <c>cid:</c>) images that the indexer historically skipped: before inline
/// capture, <see cref="MessageParser"/> enumerated <c>mime.Attachments</c>,
/// which by MimeKit's definition excludes inline images. So a photographed
/// document embedded inline (Apple Mail, forwarded posts) got no attachments
/// row, never entered extraction/OCR, and was invisible to search.
///
/// The parser + <see cref="MaildirAttachmentReader"/> now both enumerate through
/// <see cref="MessageParts.Indexable"/>, which appends inline images *after* the
/// existing attachments — so existing part_index values are unchanged and this
/// backfill is purely additive: for each candidate message it re-parses the
/// .eml, computes the full indexable-part set, and inserts only the rows whose
/// part_index isn't already present. Inline images extract to
/// <c>status='unsupported'</c>, which the embedder's image-OCR pass then picks
/// up under its size/dimension gate.
///
/// Candidate set is messages whose HTML references a <c>cid:</c> part (the inline
/// pattern) — far cheaper than re-parsing all 80k messages, and it covers the
/// gap. Idempotent (INSERT OR IGNORE on the missing rows); safe to ^C and resume
/// via the id cursor.
/// </summary>
internal static class BackfillInlineImagesCommand
{
    public static Command Build()
    {
        var limitOpt = new Option<int?>("--limit") { Description = "Cap the number of candidate messages processed this run (dry-run a slice first)." };
        var batchOpt = new Option<int>("--batch") { Description = "Messages fetched per DB roundtrip. Default 100.", DefaultValueFactory = _ => 100 };
        var dryRunOpt = new Option<bool>("--dry-run") { Description = "Report how many inline-image rows would be added, without writing anything." };
        var messageIdOpt = new Option<long?>("--message-id") { Description = "Backfill just this one message (by messages.id), bypassing the candidate scan. For targeted ops/debugging." };

        var cmd = new Command("backfill-inline-images", "Add attachment rows for inline (cid:) images the indexer skipped, so they enter extraction/OCR and become searchable.")
        {
            limitOpt,
            batchOpt,
            dryRunOpt,
            messageIdOpt,
        };

        cmd.SetAction(parse => Run(
            limit: parse.GetValue(limitOpt),
            batch: Math.Max(1, parse.GetValue(batchOpt)),
            dryRun: parse.GetValue(dryRunOpt),
            messageId: parse.GetValue(messageIdOpt)));
        return cmd;
    }

    private static int Run(int? limit, int batch, bool dryRun, long? messageId)
    {
        using var sp = CliServices.Build();
        return Execute(sp, limit, batch, dryRun, messageId, Console.Out, Console.Error);
    }

    internal static int Execute(IServiceProvider sp, int? limit, int batch, bool dryRun, long? messageId, TextWriter @out, TextWriter err)
    {
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();

        var maildirRoot = PathExpansion.Expand(sp.GetRequiredService<IOptions<IngestOptions>>().Value.MaildirRoot);
        if (string.IsNullOrEmpty(maildirRoot) || !Directory.Exists(maildirRoot))
        {
            err.WriteLine($"Maildir root not found: '{maildirRoot}'. Set Ingest__MaildirRoot or fix appsettings.json.");
            return 2;
        }

        var extractor = sp.GetRequiredService<AttachmentTextExtractor>();
        var messages = sp.GetRequiredService<MessageRepository>();
        var connections = sp.GetRequiredService<ConnectionFactory>();

        long totalCandidates;
        using (var conn = connections.Open())
        using (var count = conn.CreateCommand())
        {
            if (messageId is { } mid)
            {
                count.CommandText = "SELECT COUNT(*) FROM messages WHERE id = $id AND deleted_at IS NULL;";
                count.Parameters.AddWithValue("$id", mid);
            }
            else
            {
                count.CommandText = CandidatePredicate("SELECT COUNT(*)");
            }
            totalCandidates = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (totalCandidates == 0)
        {
            @out.WriteLine(messageId is { } m
                ? $"Message {m} not found (or soft-deleted). Nothing to do."
                : "No messages reference inline (cid:) images. Nothing to do.");
            return 0;
        }

        var ceiling = limit is { } l ? Math.Min(l, totalCandidates) : totalCandidates;
        @out.WriteLine($"Candidate messages (reference a cid: part): {totalCandidates:N0}.");
        if (limit is not null) @out.WriteLine($"Limit: processing at most {ceiling:N0} message(s).");
        @out.WriteLine(dryRun ? "Mode: DRY RUN (no writes)." : "Mode: writing new inline-image rows.");
        @out.WriteLine();

        long processed = 0, messagesWithNewRows = 0, rowsAdded = 0, parseFailures = 0, missingFiles = 0;
        var statusCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        long cursor = 0;

        while (limit is null || processed < limit)
        {
            var pageSize = limit is { } pl ? Math.Min(batch, pl - (int)processed) : batch;
            var page = messageId is { } mid ? LoadSingle(connections, mid) : LoadPage(connections, cursor, pageSize);
            if (page.Count == 0) break;

            foreach (var msg in page)
            {
                cursor = msg.Id;
                processed++;

                var maildirFile = Path.Combine(maildirRoot, msg.MaildirPath.Replace('/', Path.DirectorySeparatorChar), msg.MaildirFilename);
                if (!File.Exists(maildirFile)) { missingFiles++; continue; }

                MimeMessage mime;
                try
                {
                    using var stream = File.OpenRead(maildirFile);
                    mime = MimeMessage.Load(stream);
                }
                catch (Exception ex)
                {
                    parseFailures++;
                    err.WriteLine($"  msg {msg.Id}: parse failed ({ex.GetType().Name}: {ex.Message}); skipping.");
                    continue;
                }

                var parts = MessageParts.Indexable(mime);
                var existing = messages.GetAttachmentPartIndexes(msg.Id);

                var toAdd = new List<ParsedAttachment>();
                for (int i = 0; i < parts.Count; i++)
                {
                    if (existing.Contains(i)) continue; // never disturb existing rows
                    var entity = parts[i];
                    var fileName = Normalize(entity.ContentDisposition?.FileName ?? entity.ContentType?.Name);
                    var contentType = entity.ContentType?.MimeType;
                    long? size = entity is MimePart p && p.Content is { Stream: { } s } && s.CanSeek ? s.Length : null;

                    var result = extractor.Extract(entity, fileName, contentType, size);
                    toAdd.Add(new ParsedAttachment(i, fileName, contentType, size, result.Text, result.Status));
                    statusCounts.TryGetValue(result.Status, out var prior);
                    statusCounts[result.Status] = prior + 1;
                }

                if (toAdd.Count == 0) continue;
                messagesWithNewRows++;
                rowsAdded += toAdd.Count;
                if (!dryRun) messages.AddInlineAttachments(msg.Id, toAdd);

                if (processed % 100 == 0)
                    @out.WriteLine($"  ... {processed:N0}/{ceiling:N0} messages scanned, {rowsAdded:N0} inline row(s) {(dryRun ? "found" : "added")}");
            }

            if (messageId is not null) break; // single-message mode: one page only
        }

        @out.WriteLine();
        @out.WriteLine($"Scanned {processed:N0} message(s); {(dryRun ? "would add" : "added")} {rowsAdded:N0} inline-image row(s) across {messagesWithNewRows:N0} message(s).");
        if (statusCounts.Count > 0)
        {
            @out.WriteLine("Extraction status of added rows:");
            foreach (var (status, n) in statusCounts.OrderByDescending(kv => kv.Value))
                @out.WriteLine($"  {status,-12} {n:N0}");
        }
        if (missingFiles > 0) @out.WriteLine($"Skipped {missingFiles:N0} message(s) whose .eml was missing (mbsync moved it; re-run after a rescan).");
        if (parseFailures > 0) @out.WriteLine($"Skipped {parseFailures:N0} message(s) that failed to parse.");
        if (!dryRun && rowsAdded > 0)
            @out.WriteLine("\nInline images are 'unsupported'; the embedder's image-OCR pass will process them (behind its size/dimension gate) on its next cycles.");
        return 0;
    }

    // Messages whose HTML references a cid: part — the inline-image pattern.
    private static string CandidatePredicate(string select) => $"""
        {select}
        FROM messages m
        WHERE m.deleted_at IS NULL
          AND m.body_html LIKE '%cid:%'
        """;

    private static IReadOnlyList<MessageRow> LoadPage(ConnectionFactory connections, long cursor, int pageSize)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.maildir_path, m.maildir_filename
            FROM messages m
            WHERE m.deleted_at IS NULL
              AND m.body_html LIKE '%cid:%'
              AND m.id > $cursor
            ORDER BY m.id
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$cursor", cursor);
        cmd.Parameters.AddWithValue("$limit", pageSize);

        var list = new List<MessageRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new MessageRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }

    private static IReadOnlyList<MessageRow> LoadSingle(ConnectionFactory connections, long messageId)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, maildir_path, maildir_filename FROM messages WHERE id = $id AND deleted_at IS NULL;";
        cmd.Parameters.AddWithValue("$id", messageId);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? [new MessageRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2))]
            : [];
    }

    private static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private sealed record MessageRow(long Id, string MaildirPath, string MaildirFilename);
}
