using System.Text;
using System.Text.Json;
using Mailvec.Core.Attachments;
using Mailvec.Core.Models;
using Mailvec.Core.Parsing;
using Mailvec.Core.Search;
using Microsoft.Data.Sqlite;

namespace Mailvec.Core.Data;

/// <summary>
/// Result of <see cref="MessageRepository.Upsert"/>. Implicitly converts to
/// <see cref="long"/> so callers that only need the row id can keep their
/// existing single-value usage; callers that care about content invalidation
/// destructure or read <see cref="ContentChanged"/> explicitly.
/// </summary>
public readonly record struct UpsertOutcome(long Id, bool ContentChanged, bool IsNewInsert)
{
    public static implicit operator long(UpsertOutcome o) => o.Id;

    /// <summary>
    /// True iff <see cref="MessageRepository.Upsert"/> reset the attachments
    /// table for this message (either it was a new insert, or the body's
    /// content_hash changed). The indexer uses this to know whether
    /// attachment-text extraction needs to re-run; when neither flag is set,
    /// existing extracted_text rows are preserved verbatim.
    /// </summary>
    public bool AttachmentsReset => IsNewInsert || ContentChanged;
}

public sealed class MessageRepository(ConnectionFactory connections)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>
    /// Insert or update a message. Keyed on Message-ID (RFC 5322); resending the
    /// same Message-ID with a different Maildir path updates the path in place,
    /// which covers mbsync's new/ -> cur/ rename and the ingest re-scan path.
    /// Returns the row id and whether the message body content changed compared
    /// to the prior row (always false on a fresh insert; true only when a row
    /// with the same Message-ID existed and its <c>content_hash</c> differed
    /// from the one being written). Callers use the flag to invalidate stale
    /// embeddings; see <see cref="ChunkRepository.ClearEmbeddingsForMessage"/>.
    /// </summary>
    public UpsertOutcome Upsert(ParsedMessage parsed, string folder, string maildirRelativePath, string maildirFilename, DateTimeOffset indexedAt)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        using var conn = connections.Open();
        using var tx = conn.BeginTransaction();

        // Read the prior row's content_hash inside the same transaction so
        // there's no race against another writer mutating it underneath us.
        // priorHash is null for fresh inserts and for rows recorded before
        // schema v3 (where content_hash hadn't been backfilled yet) — both
        // mean "treat as unchanged" so we don't churn embeddings on every
        // first re-scan after migration.
        string? priorHash = null;
        using (var probe = conn.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT content_hash FROM messages WHERE message_id = $mid";
            probe.Parameters.AddWithValue("$mid", parsed.MessageId);
            var raw = probe.ExecuteScalar();
            priorHash = raw is string s ? s : null;
        }
        var contentChanged = priorHash is not null && !string.Equals(priorHash, parsed.ContentHash, StringComparison.Ordinal);
        var isNewInsert = priorHash is null;

        long id;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO messages (
                    message_id, thread_id, maildir_path, maildir_filename, folder,
                    subject, from_address, from_name, to_addresses, cc_addresses,
                    date_sent, date_received, size_bytes, has_attachments, attachment_names,
                    attachment_text, body_text, body_html, raw_headers, indexed_at, deleted_at, content_hash
                ) VALUES (
                    $message_id, $thread_id, $maildir_path, $maildir_filename, $folder,
                    $subject, $from_address, $from_name, $to_addresses, $cc_addresses,
                    $date_sent, $date_received, $size_bytes, $has_attachments, $attachment_names,
                    $attachment_text, $body_text, $body_html, $raw_headers, $indexed_at, NULL, $content_hash
                )
                ON CONFLICT(message_id) DO UPDATE SET
                    thread_id        = excluded.thread_id,
                    maildir_path     = excluded.maildir_path,
                    maildir_filename = excluded.maildir_filename,
                    folder           = excluded.folder,
                    subject          = excluded.subject,
                    from_address     = excluded.from_address,
                    from_name        = excluded.from_name,
                    to_addresses     = excluded.to_addresses,
                    cc_addresses     = excluded.cc_addresses,
                    date_sent        = excluded.date_sent,
                    date_received    = excluded.date_received,
                    size_bytes       = excluded.size_bytes,
                    has_attachments  = excluded.has_attachments,
                    attachment_names = excluded.attachment_names,
                    attachment_text  = excluded.attachment_text,
                    body_text        = excluded.body_text,
                    body_html        = excluded.body_html,
                    raw_headers      = excluded.raw_headers,
                    indexed_at       = excluded.indexed_at,
                    deleted_at       = NULL,
                    content_hash     = excluded.content_hash
                RETURNING id;
                """;

            var attachmentNames = BuildAttachmentNames(parsed.Attachments);
            var attachmentText = BuildAttachmentText(parsed.Attachments);

            cmd.Parameters.AddWithValue("$message_id", parsed.MessageId);
            cmd.Parameters.AddWithValue("$thread_id", parsed.ThreadId);
            cmd.Parameters.AddWithValue("$maildir_path", maildirRelativePath);
            cmd.Parameters.AddWithValue("$maildir_filename", maildirFilename);
            cmd.Parameters.AddWithValue("$folder", folder);
            cmd.Parameters.AddWithValue("$subject", (object?)parsed.Subject ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$from_address", (object?)parsed.FromAddress ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$from_name", (object?)parsed.FromName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$to_addresses", JsonSerializer.Serialize(parsed.ToAddresses, JsonOpts));
            cmd.Parameters.AddWithValue("$cc_addresses", JsonSerializer.Serialize(parsed.CcAddresses, JsonOpts));
            cmd.Parameters.AddWithValue("$date_sent", (object?)parsed.DateSent?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$date_received", indexedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$size_bytes", parsed.SizeBytes);
            cmd.Parameters.AddWithValue("$has_attachments", parsed.HasAttachments ? 1 : 0);
            cmd.Parameters.AddWithValue("$attachment_names", (object?)attachmentNames ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$attachment_text", (object?)attachmentText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$body_text", (object?)parsed.BodyText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$body_html", (object?)parsed.BodyHtml ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$raw_headers", parsed.RawHeaders);
            cmd.Parameters.AddWithValue("$indexed_at", indexedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$content_hash", parsed.ContentHash);

            var idObj = cmd.ExecuteScalar()
                ?? throw new InvalidOperationException("Upsert returned no id");
            id = Convert.ToInt64(idObj, System.Globalization.CultureInfo.InvariantCulture);
        }

        // Only rewrite the attachments table when the row is new or the body
        // content changed. On a no-op rescan we'd otherwise wipe and reinsert
        // the same metadata, which would also throw away extracted_text and
        // force the embedder to re-extract every attachment on every scan.
        if (isNewInsert || contentChanged)
        {
            ReplaceAttachments(conn, tx, id, parsed.Attachments);
        }
        tx.Commit();
        return new UpsertOutcome(id, contentChanged, isNewInsert);
    }

    private static string? BuildAttachmentNames(IReadOnlyList<ParsedAttachment> attachments)
    {
        if (attachments.Count == 0) return null;
        var names = attachments
            .Select(a => a.FileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
        return names.Count == 0 ? null : string.Join(' ', names);
    }

    /// <summary>
    /// Concatenate every attachment's extracted text into a single
    /// space-joined blob that messages_fts can BM25-index. Mirrors the
    /// <c>group_concat(extracted_text, ' ')</c> backfill in migration 005,
    /// so a fresh insert and a v4-upgrade backfill produce the same FTS
    /// content. Returns null when no attachment carries indexable text;
    /// FTS5 treats NULL columns as empty, which is what we want.
    /// </summary>
    private static string? BuildAttachmentText(IReadOnlyList<ParsedAttachment> attachments)
    {
        if (attachments.Count == 0) return null;
        var texts = attachments
            .Where(a => !string.IsNullOrEmpty(a.ExtractedText))
            .Select(a => a.ExtractedText!)
            .ToList();
        return texts.Count == 0 ? null : string.Join(' ', texts);
    }

    private static void ReplaceAttachments(SqliteConnection conn, SqliteTransaction tx, long messageId, IReadOnlyList<ParsedAttachment> attachments)
    {
        // chunks.attachment_id has ON DELETE CASCADE → SQLite will silently
        // cascade-delete the attachment-sourced chunks rows when we DELETE
        // FROM attachments below. chunk_embeddings is a vec0 virtual table
        // that does NOT participate in FK cascade, so its rows for those
        // chunk_ids would be left orphaned. chunks.id is INTEGER PRIMARY KEY
        // without AUTOINCREMENT, so future inserts pick MAX(id)+1 — eventually
        // a new chunk lands on an orphan rowid and the embedder blows up with
        // `UNIQUE constraint failed on chunk_embeddings primary key` and the
        // message gets stuck unembedded. Clear the orphaned-by-cascade rows
        // here while the chunks rows still exist and are joinable. Body
        // chunks (attachment_id IS NULL) are unaffected by this cascade; the
        // scanner's ClearEmbeddingsForMessage call cleans them in a later tx.
        // The matching guard for the messages-cascade path lives in
        // PurgeSoftDeleted.
        using (var delEmbeddings = conn.CreateCommand())
        {
            delEmbeddings.Transaction = tx;
            delEmbeddings.CommandText = """
                DELETE FROM chunk_embeddings
                WHERE chunk_id IN (
                    SELECT id FROM chunks
                    WHERE message_id = $mid AND attachment_id IS NOT NULL
                )
                """;
            delEmbeddings.Parameters.AddWithValue("$mid", messageId);
            delEmbeddings.ExecuteNonQuery();
        }

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM attachments WHERE message_id = $mid";
            del.Parameters.AddWithValue("$mid", messageId);
            del.ExecuteNonQuery();
        }

        if (attachments.Count == 0) return;

        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO attachments(
                message_id, part_index, filename, content_type, size_bytes,
                extracted_text, extracted_at, extraction_status)
            VALUES ($mid, $idx, $name, $ct, $size, $text, $at, $status);
            """;
        var pMid = ins.Parameters.Add("$mid", SqliteType.Integer);
        var pIdx = ins.Parameters.Add("$idx", SqliteType.Integer);
        var pName = ins.Parameters.Add("$name", SqliteType.Text);
        var pCt = ins.Parameters.Add("$ct", SqliteType.Text);
        var pSize = ins.Parameters.Add("$size", SqliteType.Integer);
        var pText = ins.Parameters.Add("$text", SqliteType.Text);
        var pAt = ins.Parameters.Add("$at", SqliteType.Text);
        var pStatus = ins.Parameters.Add("$status", SqliteType.Text);
        pMid.Value = messageId;

        var stamp = DateTimeOffset.UtcNow.ToString("O");
        foreach (var a in attachments)
        {
            pIdx.Value = a.PartIndex;
            pName.Value = (object?)a.FileName ?? DBNull.Value;
            pCt.Value = (object?)a.ContentType ?? DBNull.Value;
            pSize.Value = (object?)a.SizeBytes ?? DBNull.Value;
            pText.Value = (object?)a.ExtractedText ?? DBNull.Value;
            pStatus.Value = (object?)a.ExtractionStatus ?? DBNull.Value;
            // Stamp extracted_at whenever extraction was attempted (any non-null
            // status). Null status means the parser ran without an extractor —
            // leave the timestamp NULL so a future scan with the extractor
            // wired in can detect "never tried" and run.
            pAt.Value = a.ExtractionStatus is null ? DBNull.Value : (object)stamp;
            ins.ExecuteNonQuery();
        }
    }

    private IReadOnlyList<Attachment> GetAttachmentsForMessage(SqliteConnection conn, long messageId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, part_index, filename, content_type, size_bytes,
                   extracted_text, extracted_at, extraction_status
            FROM attachments
            WHERE message_id = $mid
            ORDER BY part_index;
            """;
        cmd.Parameters.AddWithValue("$mid", messageId);

        var list = new List<Attachment>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Attachment(
                PartIndex: reader.GetInt32(1),
                FileName: reader.IsDBNull(2) ? null : reader.GetString(2),
                ContentType: reader.IsDBNull(3) ? null : reader.GetString(3),
                SizeBytes: reader.IsDBNull(4) ? null : reader.GetInt64(4),
                Id: reader.GetInt64(0),
                ExtractedText: reader.IsDBNull(5) ? null : reader.GetString(5),
                ExtractedAt: reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
                ExtractionStatus: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return list;
    }

    public Message? GetById(long id)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);

        Message? msg;
        using (var reader = cmd.ExecuteReader())
        {
            msg = reader.Read() ? Map(reader) : null;
        }
        if (msg is null) return null;
        return msg with { Attachments = GetAttachmentsForMessage(conn, msg.Id) };
    }

    public Message? GetByMessageId(string messageId)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM messages WHERE message_id = $mid";
        cmd.Parameters.AddWithValue("$mid", messageId);

        Message? msg;
        using (var reader = cmd.ExecuteReader())
        {
            msg = reader.Read() ? Map(reader) : null;
        }
        if (msg is null) return null;
        return msg with { Attachments = GetAttachmentsForMessage(conn, msg.Id) };
    }

    /// <summary>
    /// Filter-only browse — used by `search_emails` when no query is supplied
    /// and as the implementation of "recent emails" / "find by sender" tools.
    /// Sorts by date_sent DESC; messages with NULL date_sent fall to the bottom
    /// (we don't want them mixed in with current mail when the user asks "what's
    /// recent").
    /// </summary>
    public IReadOnlyList<Message> BrowseByFilters(SearchFilters filters, int limit)
    {
        ArgumentNullException.ThrowIfNull(filters);

        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        var sql = new StringBuilder("""
            SELECT m.* FROM messages m
            WHERE m.deleted_at IS NULL
            """);
        SearchFilterSql.Append(sql, cmd, filters);
        sql.Append("\nORDER BY m.date_sent IS NULL, m.date_sent DESC\nLIMIT $limit;");
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<Message>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(Map(reader));
        return results;
    }

    /// <summary>
    /// All messages in a thread, oldest first. Looks up by either the SQLite
    /// internal id or the RFC Message-ID header. Returns an empty list if the
    /// id/Message-ID matches a message with no thread_id (lone message).
    /// </summary>
    public IReadOnlyList<Message> GetThreadByMessageId(long? id, string? messageId)
    {
        if (id is null && string.IsNullOrEmpty(messageId))
            throw new ArgumentException("Provide id or messageId.");

        using var conn = connections.Open();

        // First resolve to a thread_id. We can't just JOIN — thread_id may be
        // NULL (lone message), in which case we still want to return that one
        // message rather than empty.
        string? threadId;
        long? rootId;
        using (var resolve = conn.CreateCommand())
        {
            resolve.CommandText = id is not null
                ? "SELECT id, thread_id FROM messages WHERE id = $k"
                : "SELECT id, thread_id FROM messages WHERE message_id = $k";
            resolve.Parameters.AddWithValue("$k", (object?)id ?? messageId!);
            using var r = resolve.ExecuteReader();
            if (!r.Read()) return [];
            rootId = r.GetInt64(0);
            threadId = r.IsDBNull(1) ? null : r.GetString(1);
        }

        using var cmd = conn.CreateCommand();
        if (threadId is null)
        {
            // Lone message — return just it.
            cmd.CommandText = "SELECT * FROM messages WHERE id = $id AND deleted_at IS NULL";
            cmd.Parameters.AddWithValue("$id", rootId!.Value);
        }
        else
        {
            cmd.CommandText = """
                SELECT * FROM messages
                WHERE thread_id = $tid AND deleted_at IS NULL
                ORDER BY date_sent IS NULL, date_sent ASC, id ASC
                """;
            cmd.Parameters.AddWithValue("$tid", threadId);
        }

        var results = new List<Message>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(Map(reader));
        return results;
    }

    /// <summary>
    /// One row per non-empty folder, sorted by name. Soft-deleted messages are
    /// excluded from the count. Useful for the `list_folders` MCP tool so
    /// Claude knows what folders exist before filtering by one.
    /// </summary>
    public IReadOnlyList<FolderStats> FolderStats()
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                folder,
                COUNT(*)         AS msg_count,
                MIN(date_sent)   AS oldest_date,
                MAX(date_sent)   AS latest_date
            FROM messages
            WHERE deleted_at IS NULL
            GROUP BY folder
            ORDER BY folder;
            """;

        var results = new List<FolderStats>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new FolderStats(
                Folder: reader.GetString(0),
                MessageCount: reader.GetInt64(1),
                OldestDate: ReadNullableDate(reader, "oldest_date"),
                LatestDate: ReadNullableDate(reader, "latest_date")));
        }
        return results;
    }

    public int CountAll()
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE deleted_at IS NULL";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Whole-archive summary: total non-deleted message count plus the
    /// oldest/newest date_sent values. Surfaced on every search response so
    /// Claude can size its filters against actual archive scope.
    ///
    /// MIN/MAX use idx_messages_date_sent so they're cheap; COUNT(*) is a
    /// scan but sub-100ms on archives in the hundreds of thousands. If this
    /// ever becomes hot enough to matter, cache in a singleton with a short
    /// TTL or add a partial index on deleted_at.
    /// </summary>
    public ArchiveStats GetArchiveStats()
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*)       AS total,
                MIN(date_sent) AS oldest,
                MAX(date_sent) AS latest
            FROM messages
            WHERE deleted_at IS NULL;
            """;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return new ArchiveStats(0, null, null);
        }

        return new ArchiveStats(
            TotalMessages: reader.GetInt64(0),
            OldestDate: ReadNullableDate(reader, "oldest"),
            LatestDate: ReadNullableDate(reader, "latest"));
    }

    /// <summary>
    /// Lazily streams unembedded, undeleted messages plus their extracted
    /// attachment text for the embedder. Messages with no body text are still
    /// returned so the worker can stamp them with zero chunks instead of
    /// leaving them stuck forever. Each <see cref="UnembeddedMessage"/>
    /// carries a list of attachment payloads (id + filename + extracted text)
    /// for attachments where extraction status is 'done'; the embedder chunks
    /// those separately with <c>source='attachment'</c> so search hits can
    /// be traced back to the document that matched.
    /// </summary>
    public IEnumerable<UnembeddedMessage> EnumerateUnembedded(int batchSize = 50)
    {
        using var conn = connections.Open();

        // Fetch the message rows first, then load attachment payloads in a
        // second query per message. Two-step is simpler than a single GROUP_CONCAT
        // and avoids the per-row cost on the (common) bodies-only path.
        var rows = new List<(long Id, string BodyText, string? Subject, string? AttachmentNames)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, COALESCE(body_text, '') AS body_text, subject, attachment_names
                FROM messages
                WHERE embedded_at IS NULL
                  AND deleted_at IS NULL
                ORDER BY id
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", batchSize);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        foreach (var row in rows)
        {
            var attachmentTexts = LoadAttachmentTexts(conn, row.Id);
            yield return new UnembeddedMessage(row.Id, row.BodyText, row.Subject, row.AttachmentNames, attachmentTexts);
        }
    }

    /// <summary>
    /// Scanned / image-only PDF attachments (extraction_status='no_text') whose
    /// text the embedder's OCR pass should try to recover. Returns enough to
    /// locate the bytes in the Maildir. Materialised (not streamed) because the
    /// caller does slow async OCR between items.
    /// </summary>
    public IReadOnlyList<OcrCandidate> EnumerateAttachmentsNeedingOcr(int batchSize)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, a.part_index, m.id, m.message_id, m.maildir_path, m.maildir_filename, m.folder
            FROM attachments a
            JOIN messages m ON m.id = a.message_id
            WHERE a.extraction_status = $noText
              AND lower(a.filename) LIKE '%.pdf'
              AND m.deleted_at IS NULL
            ORDER BY a.id
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$noText", AttachmentTextExtractor.StatusNoText);
        cmd.Parameters.AddWithValue("$limit", batchSize);

        var list = new List<OcrCandidate>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new OcrCandidate(
                AttachmentId: reader.GetInt64(0),
                PartIndex: reader.GetInt32(1),
                MessageId: reader.GetInt64(2),
                MessageIdHeader: reader.GetString(3),
                MaildirPath: reader.GetString(4),
                MaildirFilename: reader.GetString(5),
                Folder: reader.GetString(6)));
        }
        return list;
    }

    /// <summary>
    /// Image attachments the indexer left at 'unsupported' that are worth a
    /// vision-OCR attempt: a real image type (GIFs excluded — animated /
    /// decorative), above the byte pre-filter, parent not soft-deleted. This is
    /// stage 1 of the gate (cheap, in SQL); stage 2 (decode dimensions / aspect)
    /// runs in <c>AttachmentOcrService</c> after the bytes are rendered. Mirrors
    /// <see cref="EnumerateAttachmentsNeedingOcr"/>; reuses <see cref="OcrCandidate"/>.
    /// </summary>
    // Which attachments the image-OCR pass treats as candidate images. Primary
    // signal is content_type image/* (minus GIF — animated/banner strips, low
    // text yield). Senders also ship real photos as application/octet-stream or
    // with no Content-Type at all (forwarded phone pics, "IMG_1234.jpeg" saved
    // by a mailer that forgot the type), so a decodable image extension on a
    // generic content-type qualifies too; ImageRenderer.TryNormalize is the
    // backstop that marks any non-image binary 'failed'. GIF stays excluded in
    // both arms. Shared verbatim by the OCR candidate query and the pending-count
    // query so /health, the tray, and the embedder never disagree.
    private const string ImageOcrMatch = """
        (
          (lower(a.content_type) LIKE 'image/%' AND lower(a.content_type) <> 'image/gif')
          OR (
            (a.content_type IS NULL OR lower(a.content_type) IN ('application/octet-stream', ''))
            AND (
              lower(a.filename) LIKE '%.png' OR lower(a.filename) LIKE '%.jpg'
              OR lower(a.filename) LIKE '%.jpeg' OR lower(a.filename) LIKE '%.webp'
              OR lower(a.filename) LIKE '%.bmp' OR lower(a.filename) LIKE '%.tif'
              OR lower(a.filename) LIKE '%.tiff'
            )
          )
        )
        """;

    public IReadOnlyList<OcrCandidate> EnumerateImagesNeedingOcr(int batchSize, long minBytes)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT a.id, a.part_index, m.id, m.message_id, m.maildir_path, m.maildir_filename, m.folder
            FROM attachments a
            JOIN messages m ON m.id = a.message_id
            WHERE a.extraction_status = $unsupported
              AND {ImageOcrMatch}
              AND a.size_bytes >= $minBytes
              AND m.deleted_at IS NULL
            ORDER BY a.id
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$unsupported", AttachmentTextExtractor.StatusUnsupported);
        cmd.Parameters.AddWithValue("$minBytes", minBytes);
        cmd.Parameters.AddWithValue("$limit", batchSize);

        var list = new List<OcrCandidate>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new OcrCandidate(
                AttachmentId: reader.GetInt64(0),
                PartIndex: reader.GetInt32(1),
                MessageId: reader.GetInt64(2),
                MessageIdHeader: reader.GetString(3),
                MaildirPath: reader.GetString(4),
                MaildirFilename: reader.GetString(5),
                Folder: reader.GetString(6)));
        }
        return list;
    }

    /// <summary>
    /// Mark an image attachment terminally as <c>no_text</c> after the OCR pass
    /// decided it carries nothing useful — gated out by the post-decode
    /// dimension/aspect check, or the vision model returned empty text. This
    /// moves it off the 'unsupported' image queue so it isn't re-read and
    /// re-decoded every cycle. (Decode *failures* go through
    /// <see cref="MarkAttachmentOcrFailed"/> instead.)
    /// </summary>
    public void MarkAttachmentImageNoText(long attachmentId)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE attachments SET extraction_status = $noText WHERE id = $id;";
        cmd.Parameters.AddWithValue("$noText", AttachmentTextExtractor.StatusNoText);
        cmd.Parameters.AddWithValue("$id", attachmentId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Pipeline counts for the OCR stage, split by source (scanned PDFs vs image
    /// attachments), surfaced by <c>/health</c>, the tray, and
    /// <c>mailvec status</c>. The pending predicates mirror
    /// <see cref="EnumerateAttachmentsNeedingOcr"/> (PDFs) and
    /// <see cref="EnumerateImagesNeedingOcr"/> (images) exactly, so the numbers
    /// match what the embedder will actually select — keep them in lockstep.
    /// <paramref name="imageMinBytes"/> is the image byte gate
    /// (<c>Embedder:ImageOcrMinBytes</c>); pass the configured value. Note the
    /// image pending count is an upper bound: the post-decode dimension/aspect
    /// gate (not expressible in SQL) drops some of these to no_text at OCR time.
    /// Recovered counts are attachments already transcribed (status='ocr'), split
    /// image vs. not. All exclude soft-deleted messages.
    /// </summary>
    public OcrStageCounts OcrCounts(long imageMinBytes)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
              (SELECT COUNT(*) FROM attachments a JOIN messages m ON m.id = a.message_id
                 WHERE a.extraction_status = $noText AND lower(a.filename) LIKE '%.pdf'
                   AND m.deleted_at IS NULL),
              (SELECT COUNT(*) FROM attachments a JOIN messages m ON m.id = a.message_id
                 WHERE a.extraction_status = $unsupported AND {ImageOcrMatch}
                   AND a.size_bytes >= $minBytes
                   AND m.deleted_at IS NULL),
              (SELECT COUNT(*) FROM attachments a JOIN messages m ON m.id = a.message_id
                 WHERE a.extraction_status = $ocr AND NOT {ImageOcrMatch}
                   AND m.deleted_at IS NULL),
              (SELECT COUNT(*) FROM attachments a JOIN messages m ON m.id = a.message_id
                 WHERE a.extraction_status = $ocr AND {ImageOcrMatch}
                   AND m.deleted_at IS NULL)
            """;
        cmd.Parameters.AddWithValue("$noText", AttachmentTextExtractor.StatusNoText);
        cmd.Parameters.AddWithValue("$unsupported", AttachmentTextExtractor.StatusUnsupported);
        cmd.Parameters.AddWithValue("$ocr", AttachmentTextExtractor.StatusOcr);
        cmd.Parameters.AddWithValue("$minBytes", imageMinBytes);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new OcrStageCounts(
            PdfPending: reader.GetInt64(0),
            ImagePending: reader.GetInt64(1),
            PdfRecovered: reader.GetInt64(2),
            ImageRecovered: reader.GetInt64(3));
    }

    /// <summary>
    /// Persist OCR-recovered text for an attachment (status='ocr'), rebuild the
    /// parent message's denormalized FTS <c>attachment_text</c> so keyword
    /// search sees it, and re-queue the message for embedding by clearing
    /// embedded_at (the re-embed's ReplaceChunksForMessage replaces its chunks).
    /// One transaction. The messages UPDATE fires the FTS sync trigger.
    /// </summary>
    public void SaveOcrText(long attachmentId, long messageId, string text)
    {
        using var conn = connections.Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE attachments
                SET extracted_text = $text, extraction_status = $ocr, extracted_at = $now
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$text", text);
            cmd.Parameters.AddWithValue("$ocr", AttachmentTextExtractor.StatusOcr);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", attachmentId);
            cmd.ExecuteNonQuery();
        }

        var attachmentText = ConcatAttachmentText(conn, tx, messageId);
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE messages SET attachment_text = $at, embedded_at = NULL WHERE id = $id;";
            cmd.Parameters.AddWithValue("$at", (object?)attachmentText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", messageId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Space-join the message's attachment texts (any non-empty extracted_text,
    /// incl. 'ocr') for the FTS attachment_text column. Mirrors the index-time
    /// <see cref="BuildAttachmentText"/> but reads the persisted rows, since the
    /// OCR write-back runs after indexing.
    /// </summary>
    private static string? ConcatAttachmentText(SqliteConnection conn, SqliteTransaction tx, long messageId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT extracted_text FROM attachments
            WHERE message_id = $mid AND extracted_text IS NOT NULL AND LENGTH(extracted_text) > 0
            ORDER BY part_index;
            """;
        cmd.Parameters.AddWithValue("$mid", messageId);

        var texts = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) texts.Add(reader.GetString(0));
        return texts.Count == 0 ? null : string.Join(' ', texts);
    }

    /// <summary>Space-join of the message's attachment filenames, for messages.attachment_names (mirrors <see cref="BuildAttachmentNames"/> over persisted rows).</summary>
    private static string? ConcatAttachmentNames(SqliteConnection conn, SqliteTransaction tx, long messageId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT filename FROM attachments
            WHERE message_id = $mid AND filename IS NOT NULL AND LENGTH(TRIM(filename)) > 0
            ORDER BY part_index;
            """;
        cmd.Parameters.AddWithValue("$mid", messageId);

        var names = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.Count == 0 ? null : string.Join(' ', names);
    }

    /// <summary>
    /// The set of <c>part_index</c> values already present for a message. The
    /// inline-image backfill uses this to insert only the parts it's missing —
    /// never re-extracting or disturbing rows that already exist (including
    /// OCR-recovered text).
    /// </summary>
    public HashSet<int> GetAttachmentPartIndexes(long messageId)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT part_index FROM attachments WHERE message_id = $mid;";
        cmd.Parameters.AddWithValue("$mid", messageId);
        var set = new HashSet<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) set.Add(reader.GetInt32(0));
        return set;
    }

    /// <summary>
    /// Insert-only attachment rows for the inline-image backfill: adds the given
    /// rows (whose part_index the caller has confirmed are missing), then
    /// rebuilds the denormalized <c>attachment_names</c> / <c>attachment_text</c>
    /// and sets <c>has_attachments = 1</c> from the full row set. If any inserted
    /// row carries text, also clears <c>embedded_at</c> so the message re-embeds.
    /// <c>INSERT OR IGNORE</c> keeps it safe to re-run (UNIQUE(message_id,
    /// part_index) collisions are skipped). One transaction; the messages UPDATE
    /// fires the FTS sync trigger. Returns rows actually inserted.
    /// </summary>
    public int AddInlineAttachments(long messageId, IReadOnlyList<ParsedAttachment> rows)
    {
        if (rows.Count == 0) return 0;

        using var conn = connections.Open();
        using var tx = conn.BeginTransaction();

        int inserted = 0;
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT OR IGNORE INTO attachments(
                    message_id, part_index, filename, content_type, size_bytes,
                    extracted_text, extracted_at, extraction_status)
                VALUES ($mid, $idx, $name, $ct, $size, $text, $at, $status);
                """;
            var pMid = ins.Parameters.Add("$mid", SqliteType.Integer);
            var pIdx = ins.Parameters.Add("$idx", SqliteType.Integer);
            var pName = ins.Parameters.Add("$name", SqliteType.Text);
            var pCt = ins.Parameters.Add("$ct", SqliteType.Text);
            var pSize = ins.Parameters.Add("$size", SqliteType.Integer);
            var pText = ins.Parameters.Add("$text", SqliteType.Text);
            var pAt = ins.Parameters.Add("$at", SqliteType.Text);
            var pStatus = ins.Parameters.Add("$status", SqliteType.Text);
            pMid.Value = messageId;
            var stamp = DateTimeOffset.UtcNow.ToString("O");

            foreach (var a in rows)
            {
                pIdx.Value = a.PartIndex;
                pName.Value = (object?)a.FileName ?? DBNull.Value;
                pCt.Value = (object?)a.ContentType ?? DBNull.Value;
                pSize.Value = (object?)a.SizeBytes ?? DBNull.Value;
                pText.Value = (object?)a.ExtractedText ?? DBNull.Value;
                pStatus.Value = (object?)a.ExtractionStatus ?? DBNull.Value;
                pAt.Value = a.ExtractionStatus is null ? DBNull.Value : (object)stamp;
                inserted += ins.ExecuteNonQuery();
            }
        }

        if (inserted == 0)
        {
            tx.Commit();
            return 0;
        }

        var anyText = rows.Any(r => !string.IsNullOrEmpty(r.ExtractedText));
        var attachmentText = ConcatAttachmentText(conn, tx, messageId);
        var attachmentNames = ConcatAttachmentNames(conn, tx, messageId);

        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText =
                "UPDATE messages SET has_attachments = 1, attachment_names = $names, attachment_text = $text"
                + (anyText ? ", embedded_at = NULL" : "")
                + " WHERE id = $id;";
            upd.Parameters.AddWithValue("$names", (object?)attachmentNames ?? DBNull.Value);
            upd.Parameters.AddWithValue("$text", (object?)attachmentText ?? DBNull.Value);
            upd.Parameters.AddWithValue("$id", messageId);
            upd.ExecuteNonQuery();
        }

        tx.Commit();
        return inserted;
    }

    /// <summary>
    /// Mark an attachment 'failed' so the OCR pass stops re-selecting a PDF that
    /// PDFium can't even open (a poison doc would otherwise be retried forever).
    /// </summary>
    public void MarkAttachmentOcrFailed(long attachmentId)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE attachments SET extraction_status = $failed WHERE id = $id;";
        cmd.Parameters.AddWithValue("$failed", AttachmentTextExtractor.StatusFailed);
        cmd.Parameters.AddWithValue("$id", attachmentId);
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<AttachmentEmbeddingPayload> LoadAttachmentTexts(SqliteConnection conn, long messageId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, part_index, filename, extracted_text
            FROM attachments
            WHERE message_id = $mid
              AND extracted_text IS NOT NULL
              AND LENGTH(extracted_text) > 0
            ORDER BY part_index;
            """;
        cmd.Parameters.AddWithValue("$mid", messageId);

        var list = new List<AttachmentEmbeddingPayload>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AttachmentEmbeddingPayload(
                AttachmentId: reader.GetInt64(0),
                PartIndex: reader.GetInt32(1),
                FileName: reader.IsDBNull(2) ? null : reader.GetString(2),
                Text: reader.GetString(3)));
        }
        return list;
    }

    public int CountUnembedded()
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM messages
            WHERE embedded_at IS NULL AND deleted_at IS NULL
            """;
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public int MarkDeleted(IEnumerable<long> ids, DateTimeOffset deletedAt)
    {
        using var conn = connections.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE messages SET deleted_at = $at WHERE id = $id AND deleted_at IS NULL";
        var atParam = cmd.Parameters.Add("$at", SqliteType.Text);
        var idParam = cmd.Parameters.Add("$id", SqliteType.Integer);
        atParam.Value = deletedAt.ToString("O");

        var affected = 0;
        foreach (var id in ids)
        {
            idParam.Value = id;
            affected += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return affected;
    }

    public int CountSoftDeleted()
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE deleted_at IS NOT NULL";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Hard-deletes every message with deleted_at IS NOT NULL, along with its
    /// chunks, chunk_embeddings, attachments, and FTS rows. Returns the number
    /// of message rows removed.
    ///
    /// Cascade strategy:
    /// - chunks and attachments cascade on FK ON DELETE CASCADE.
    /// - The messages_ad trigger keeps messages_fts in lockstep.
    /// - chunk_embeddings is a vec0 virtual table that does NOT participate in
    ///   FK cascade, so it must be cleared explicitly BEFORE the messages
    ///   delete (otherwise the chunks rows we'd JOIN through are gone).
    /// All three statements run in a single transaction so a partial purge
    /// can't leave orphan vectors.
    /// </summary>
    public int PurgeSoftDeleted()
    {
        using var conn = connections.Open();
        using var tx = conn.BeginTransaction();

        using (var delEmbeddings = conn.CreateCommand())
        {
            delEmbeddings.Transaction = tx;
            delEmbeddings.CommandText = """
                DELETE FROM chunk_embeddings
                WHERE chunk_id IN (
                    SELECT c.id FROM chunks c
                    JOIN messages m ON m.id = c.message_id
                    WHERE m.deleted_at IS NOT NULL
                )
                """;
            delEmbeddings.ExecuteNonQuery();
        }

        int affected;
        using (var delMessages = conn.CreateCommand())
        {
            delMessages.Transaction = tx;
            delMessages.CommandText = "DELETE FROM messages WHERE deleted_at IS NOT NULL";
            affected = delMessages.ExecuteNonQuery();
        }

        tx.Commit();
        return affected;
    }

    private static Message Map(SqliteDataReader r)
    {
        return new Message
        {
            Id = r.GetInt64(r.GetOrdinal("id")),
            MessageId = r.GetString(r.GetOrdinal("message_id")),
            ThreadId = r.IsDBNull(r.GetOrdinal("thread_id")) ? null : r.GetString(r.GetOrdinal("thread_id")),
            MaildirPath = r.GetString(r.GetOrdinal("maildir_path")),
            MaildirFilename = r.GetString(r.GetOrdinal("maildir_filename")),
            Folder = r.GetString(r.GetOrdinal("folder")),
            Subject = ReadNullableString(r, "subject"),
            FromAddress = ReadNullableString(r, "from_address"),
            FromName = ReadNullableString(r, "from_name"),
            ToAddresses = DeserializeAddresses(ReadNullableString(r, "to_addresses")),
            CcAddresses = DeserializeAddresses(ReadNullableString(r, "cc_addresses")),
            DateSent = ReadNullableDate(r, "date_sent"),
            DateReceived = ReadNullableDate(r, "date_received"),
            SizeBytes = r.GetInt64(r.GetOrdinal("size_bytes")),
            HasAttachments = r.GetInt32(r.GetOrdinal("has_attachments")) != 0,
            BodyText = ReadNullableString(r, "body_text"),
            BodyHtml = ReadNullableString(r, "body_html"),
            RawHeaders = ReadNullableString(r, "raw_headers"),
            IndexedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("indexed_at")), System.Globalization.CultureInfo.InvariantCulture),
            EmbeddedAt = ReadNullableDate(r, "embedded_at"),
            DeletedAt = ReadNullableDate(r, "deleted_at"),
        };
    }

    private static string? ReadNullableString(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : r.GetString(ord);
    }

    private static DateTimeOffset? ReadNullableDate(SqliteDataReader r, string column)
    {
        var s = ReadNullableString(r, column);
        return s is null ? null : DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<EmailAddress> DeserializeAddresses(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        return JsonSerializer.Deserialize<List<EmailAddress>>(json, JsonOpts) ?? [];
    }
}

/// <summary>
/// OCR-stage counts split by source. <see cref="Pending"/> / <see cref="Recovered"/>
/// are the pipeline totals; the per-source fields let the UI show the split
/// (scanned PDFs vs image attachments).
/// </summary>
public sealed record OcrStageCounts(long PdfPending, long ImagePending, long PdfRecovered, long ImageRecovered)
{
    public long Pending => PdfPending + ImagePending;
    public long Recovered => PdfRecovered + ImageRecovered;
}

/// <summary>
/// One row's worth of work for the embedder: the message body plus any
/// attachment text payloads ready to be chunked and embedded.
/// </summary>
public sealed record UnembeddedMessage(
    long Id,
    string BodyText,
    string? Subject,
    string? AttachmentNames,
    IReadOnlyList<AttachmentEmbeddingPayload> Attachments);

public sealed record AttachmentEmbeddingPayload(
    long AttachmentId,
    int PartIndex,
    string? FileName,
    string Text);

/// <summary>
/// A scanned-PDF attachment the embedder's OCR pass should process, plus the
/// message fields needed to read its bytes from the Maildir.
/// </summary>
public sealed record OcrCandidate(
    long AttachmentId,
    int PartIndex,
    long MessageId,
    string MessageIdHeader,
    string MaildirPath,
    string MaildirFilename,
    string Folder)
{
    /// <summary>Minimal Message for <see cref="MaildirAttachmentReader"/> (only the path fields are read).</summary>
    public Message ToMessage() => new()
    {
        Id = MessageId,
        MessageId = MessageIdHeader,
        MaildirPath = MaildirPath,
        MaildirFilename = MaildirFilename,
        Folder = Folder,
        HasAttachments = true,
    };
}
