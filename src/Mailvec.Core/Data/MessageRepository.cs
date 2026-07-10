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
        // "Row exists with NULL hash" (pre-v3 legacy rows) must be
        // distinguished from "no row": both used to collapse to priorHash ==
        // null via ExecuteScalar, which misclassified legacy rows as fresh
        // inserts — AttachmentsReset then wiped their attachment rows
        // (destroying OCR-recovered text until a wasted re-OCR) and cleared
        // embedded_at, exactly the migration churn the NULL-means-unchanged
        // rule exists to prevent. A NULL prior hash on an EXISTING row means
        // "treat as unchanged".
        string? priorHash = null;
        var rowExists = false;
        using (var probe = conn.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT content_hash FROM messages WHERE message_id = $mid";
            probe.Parameters.AddWithValue("$mid", parsed.MessageId);
            using var reader = probe.ExecuteReader();
            if (reader.Read())
            {
                rowExists = true;
                priorHash = reader.IsDBNull(0) ? null : reader.GetString(0);
            }
        }
        var contentChanged = rowExists && priorHash is not null && !string.Equals(priorHash, parsed.ContentHash, StringComparison.Ordinal);
        var isNewInsert = !rowExists;

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
                    -- Folder attribution is FIRST-SEEN-WINS, not last-writer.
                    -- A message can live in several folders at once (Gmail's
                    -- All Mail + labels, self-CC INBOX + Sent); taking
                    -- excluded here made the attributed folder whichever copy
                    -- the scan happened to parse last — arbitrary, and it
                    -- flapped between scans. Keep the stored (folder, path,
                    -- filename) triple as a unit while the row is live; when
                    -- the stored copy's file vanishes, the scanner's rename-
                    -- repair pass (which detects the stale path at the end of
                    -- the same scan) repoints all three to a surviving copy.
                    -- The one exception is resurrection: deleted_at below is
                    -- reset to NULL, and a soft-deleted row's stored path is
                    -- exactly the file that disappeared — its sync_state rows
                    -- are long gone, so no repair pass would ever fix it.
                    -- Take the new copy's location in that case.
                    maildir_path     = CASE WHEN messages.deleted_at IS NULL THEN messages.maildir_path     ELSE excluded.maildir_path     END,
                    maildir_filename = CASE WHEN messages.deleted_at IS NULL THEN messages.maildir_filename ELSE excluded.maildir_filename END,
                    folder           = CASE WHEN messages.deleted_at IS NULL THEN messages.folder           ELSE excluded.folder           END,
                    subject          = excluded.subject,
                    from_address     = excluded.from_address,
                    from_name        = excluded.from_name,
                    to_addresses     = excluded.to_addresses,
                    cc_addresses     = excluded.cc_addresses,
                    date_sent        = excluded.date_sent,
                    -- date_received is "first indexed", so preserve it across
                    -- reparses (an mtime bump from an mbsync flag rewrite would
                    -- otherwise drift it to the current scan time). COALESCE
                    -- backfills legacy rows that stored NULL.
                    date_received    = COALESCE(messages.date_received, excluded.date_received),
                    size_bytes       = excluded.size_bytes,
                    has_attachments  = excluded.has_attachments,
                    -- Attachment-derived columns track the attachments table, which
                    -- ReplaceAttachments only rewrites when the row is new or the body
                    -- changed. On a no-op rescan we must KEEP the stored values: the
                    -- fresh parse of a scanned PDF is 'no_text' (empty attachment_text),
                    -- so blindly taking excluded here would wipe OCR-recovered text that
                    -- the embedder wrote via SaveOcrText — silently breaking keyword/FTS
                    -- search for that document. Gate on the same $attachments_reset the
                    -- ReplaceAttachments call below uses so the two stay in lockstep.
                    attachment_names = CASE WHEN $attachments_reset THEN excluded.attachment_names ELSE messages.attachment_names END,
                    attachment_text  = CASE WHEN $attachments_reset THEN excluded.attachment_text  ELSE messages.attachment_text  END,
                    body_text        = excluded.body_text,
                    body_html        = excluded.body_html,
                    raw_headers      = excluded.raw_headers,
                    indexed_at       = excluded.indexed_at,
                    deleted_at       = NULL,
                    -- Re-queue for embedding IN THE SAME TRANSACTION that writes
                    -- the new body_text + content_hash + FTS. The scanner also
                    -- calls ClearEmbeddingsForMessage to drop the stale chunks
                    -- promptly, but that runs in a separate transaction — if the
                    -- indexer crashed or hit SQLITE_BUSY between the two, the row
                    -- would keep a non-NULL embedded_at whose content_hash now
                    -- matches the new body, so no later rescan would ever detect
                    -- a change and the old vectors would shadow the new FTS text
                    -- forever. Clearing embedded_at here makes the re-queue
                    -- atomic with the body write; the embedder's delete-then-
                    -- insert then rebuilds the vectors. Gated on the same
                    -- $attachments_reset flag so a no-op rescan (which must keep
                    -- embedded_at to avoid re-embedding the whole archive every
                    -- scan) leaves it untouched.
                    embedded_at      = CASE WHEN $attachments_reset THEN NULL ELSE messages.embedded_at END,
                    -- Bump the re-queue counter whenever we re-queue, so an
                    -- embedder mid-write against the old content abandons its
                    -- stamp (see ChunkRepository.ReplaceChunksForMessage).
                    embed_epoch      = CASE WHEN $attachments_reset THEN messages.embed_epoch + 1 ELSE messages.embed_epoch END,
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
            // On INSERT the attachment_* values above are used directly; on
            // ON CONFLICT this flag decides whether to refresh them from the fresh
            // parse (attachments being replaced) or preserve the stored values
            // (no-op rescan — mirrors the ReplaceAttachments guard below).
            cmd.Parameters.AddWithValue("$attachments_reset", (isNewInsert || contentChanged) ? 1 : 0);
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

    /// <summary>
    /// Like <see cref="GetAttachmentsForMessage"/> but projects LENGTH(extracted_text)
    /// instead of loading the text blob — the thread view only needs the length
    /// (for get_attachment_text paging hints), and full texts can be 2M chars each.
    /// </summary>
    private IReadOnlyList<Attachment> GetAttachmentSummariesForMessage(SqliteConnection conn, long messageId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, part_index, filename, content_type, size_bytes,
                   LENGTH(extracted_text), extracted_at, extraction_status
            FROM attachments
            WHERE message_id = $mid
            ORDER BY part_index;
            """;
        cmd.Parameters.AddWithValue("$mid", messageId);

        var list = new List<Attachment>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var chars = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
            list.Add(new Attachment(
                PartIndex: reader.GetInt32(1),
                FileName: reader.IsDBNull(2) ? null : reader.GetString(2),
                ContentType: reader.IsDBNull(3) ? null : reader.GetString(3),
                SizeBytes: reader.IsDBNull(4) ? null : reader.GetInt64(4),
                Id: reader.GetInt64(0),
                ExtractedText: null,
                ExtractedAt: reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
                ExtractionStatus: reader.IsDBNull(7) ? null : reader.GetString(7),
                ExtractedTextChars: chars > 0 ? chars : null));
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
        return GetByMessageId(conn, messageId);
    }

    /// <summary>
    /// Connection-reusing overload for hot per-item loops — the scanner's
    /// reconciliation pass calls this once per stale entry, and opening a
    /// fresh connection each time (vec0 reload + PRAGMA setup) is the exact
    /// overhead its rolling ScanContext exists to avoid on the forward pass.
    /// </summary>
    public Message? GetByMessageId(SqliteConnection conn, string messageId)
    {
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
        // datetime() normalises the mixed-offset ISO-8601 stored via ToString("O")
        // so a +HH:mm sender doesn't sort as if it were UTC. A raw string sort
        // silently mis-orders "recent" across timezones.
        sql.Append("\nORDER BY m.date_sent IS NULL, datetime(m.date_sent) DESC\nLIMIT $limit;");
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<Message>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(Map(reader));
        return results;
    }

    /// <summary>
    /// All messages in a thread, oldest first. Looks up by either the SQLite
    /// internal id or the RFC Message-ID header. When the matched message has no
    /// thread_id (a lone message — notifications, marketing), returns just that
    /// one message, NOT an empty list. Returns empty only when no message
    /// matches the id/Message-ID at all. (Don't "fix" this to require a non-null
    /// thread_id — singletons are common; see CLAUDE.md.)
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
                ORDER BY date_sent IS NULL, datetime(date_sent) ASC, id ASC
                """;
            cmd.Parameters.AddWithValue("$tid", threadId);
        }

        var results = new List<Message>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read()) results.Add(Map(reader));
        }

        // Hydrate attachment rows so the thread view can list each message's
        // attachments (get_thread) without a follow-up get_email per message.
        // Only attachment-carrying messages pay the extra query, threads are
        // small, and the summary loader skips the extracted-text blobs.
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].HasAttachments)
                results[i] = results[i] with { Attachments = GetAttachmentSummariesForMessage(conn, results[i].Id) };
        }
        return results;
    }

    /// <summary>
    /// One row per non-empty folder, sorted by name. Soft-deleted messages are
    /// excluded from the count. Useful for the `list_folders` MCP tool so
    /// Claude knows what folders exist before filtering by one.
    ///
    /// Counts come from folder membership (sync_state, v8) so a message living
    /// in several folders (Gmail All Mail + labels) counts in each — matching
    /// what a folder-filtered search would return. A message counts once per
    /// folder even when a folder holds two copies of it. Falls back to the
    /// legacy attributed-folder grouping while sync_state.folder is still
    /// unpopulated (the window between the v8 migration and the scanner's
    /// next full scan).
    /// </summary>
    public IReadOnlyList<FolderStats> FolderStats()
    {
        using var conn = connections.Open();

        bool hasMembership;
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT EXISTS(SELECT 1 FROM sync_state WHERE folder IS NOT NULL)";
            hasMembership = Convert.ToInt64(probe.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
        }

        using var cmd = conn.CreateCommand();
        // oldest/latest via ORDER BY datetime() LIMIT 1 rather than MIN/MAX on
        // the raw string: date_sent mixes UTC 'Z' and '+HH:mm' offsets, so a
        // lexical MIN/MAX can pick the wrong extreme. The subquery returns the
        // original offset-bearing string so the caller still parses a correct
        // DateTimeOffset.
        cmd.CommandText = hasMembership
            ? """
              SELECT
                  ss.folder,
                  COUNT(DISTINCT ss.message_id) AS msg_count,
                  (SELECT o.date_sent FROM messages o
                     JOIN sync_state so ON so.message_id = o.message_id AND so.folder = ss.folder
                     WHERE o.deleted_at IS NULL AND o.date_sent IS NOT NULL
                     ORDER BY datetime(o.date_sent) ASC LIMIT 1) AS oldest_date,
                  (SELECT n.date_sent FROM messages n
                     JOIN sync_state sn ON sn.message_id = n.message_id AND sn.folder = ss.folder
                     WHERE n.deleted_at IS NULL AND n.date_sent IS NOT NULL
                     ORDER BY datetime(n.date_sent) DESC LIMIT 1) AS latest_date
              FROM sync_state ss
              JOIN messages m ON m.message_id = ss.message_id AND m.deleted_at IS NULL
              WHERE ss.folder IS NOT NULL
              GROUP BY ss.folder
              ORDER BY ss.folder;
              """
            : """
              SELECT
                  m.folder,
                  COUNT(*) AS msg_count,
                  (SELECT o.date_sent FROM messages o
                     WHERE o.folder = m.folder AND o.deleted_at IS NULL AND o.date_sent IS NOT NULL
                     ORDER BY datetime(o.date_sent) ASC LIMIT 1) AS oldest_date,
                  (SELECT n.date_sent FROM messages n
                     WHERE n.folder = m.folder AND n.deleted_at IS NULL AND n.date_sent IS NOT NULL
                     ORDER BY datetime(n.date_sent) DESC LIMIT 1) AS latest_date
              FROM messages m
              WHERE m.deleted_at IS NULL
              GROUP BY m.folder
              ORDER BY m.folder;
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
        // See FolderStats: datetime()-ordered subqueries instead of MIN/MAX so
        // mixed-offset timestamps compare chronologically, returning the
        // original offset-bearing string for the caller to parse.
        cmd.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM messages WHERE deleted_at IS NULL) AS total,
                (SELECT date_sent FROM messages
                   WHERE deleted_at IS NULL AND date_sent IS NOT NULL
                   ORDER BY datetime(date_sent) ASC LIMIT 1) AS oldest,
                (SELECT date_sent FROM messages
                   WHERE deleted_at IS NULL AND date_sent IS NOT NULL
                   ORDER BY datetime(date_sent) DESC LIMIT 1) AS latest;
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
    /// <param name="batchSize">Max messages returned.</param>
    /// <param name="excludeIds">
    /// Message ids to skip — the embedder's in-memory quarantine for messages
    /// whose embed calls permanently fail. Excluding in SQL matters: these
    /// are head-of-line (ORDER BY id), so filtering after the LIMIT would
    /// starve everything behind them.
    /// </param>
    public IEnumerable<UnembeddedMessage> EnumerateUnembedded(int batchSize = 50, IReadOnlyCollection<long>? excludeIds = null)
    {
        using var conn = connections.Open();

        // Fetch the message rows first, then load attachment payloads in a
        // second query per message. Two-step is simpler than a single GROUP_CONCAT
        // and avoids the per-row cost on the (common) bodies-only path.
        var rows = new List<(long Id, string BodyText, string? Subject, string? AttachmentNames, string? ContentHash, long EmbedEpoch)>();
        using (var cmd = conn.CreateCommand())
        {
            var exclusion = "";
            if (excludeIds is { Count: > 0 })
            {
                var names = excludeIds.Select((_, i) => $"$ex{i}").ToList();
                exclusion = $" AND id NOT IN ({string.Join(", ", names)})";
                var i = 0;
                foreach (var id in excludeIds) cmd.Parameters.AddWithValue($"$ex{i++}", id);
            }
            cmd.CommandText = $"""
                SELECT id, COALESCE(body_text, '') AS body_text, subject, attachment_names, content_hash, embed_epoch
                FROM messages
                WHERE embedded_at IS NULL
                  AND deleted_at IS NULL{exclusion}
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
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    // Snapshot content_hash + embed_epoch so the embedder can
                    // detect a body change OR a hash-preserving re-queue
                    // (attachment re-extraction, OCR write-back) committed
                    // during the (slow) embed call and refuse to write stale
                    // vectors. See ChunkRepository.ReplaceChunksForMessage.
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt64(5)));
            }
        }

        foreach (var row in rows)
        {
            var attachmentTexts = LoadAttachmentTexts(conn, row.Id);
            yield return new UnembeddedMessage(row.Id, row.BodyText, row.Subject, row.AttachmentNames, attachmentTexts, row.ContentHash, row.EmbedEpoch);
        }
    }

    /// <summary>
    /// Scanned / image-only PDF attachments (extraction_status='no_text') whose
    /// text the embedder's OCR pass should try to recover. Returns enough to
    /// locate the bytes in the Maildir. Materialised (not streamed) because the
    /// caller does slow async OCR between items.
    /// </summary>
    // Which 'no_text' attachments the scanned-PDF OCR pass treats as candidates.
    // The indexer classifies PDFs by content-type OR extension
    // (AttachmentTextExtractor.ResolveFormat), so a scanned PDF sent as
    // application/pdf with an empty/missing filename also lands at 'no_text' —
    // keying on the '.pdf' suffix alone stranded those, permanently unsearchable
    // and uncounted. Shared verbatim by the candidate query and OcrCounts so the
    // pending count matches what the embedder actually selects.
    private const string PdfOcrMatch =
        "(lower(a.content_type) = 'application/pdf' OR lower(a.filename) LIKE '%.pdf')";

    public IReadOnlyList<OcrCandidate> EnumerateAttachmentsNeedingOcr(int batchSize)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT a.id, a.part_index, m.id, m.message_id, m.maildir_path, m.maildir_filename, m.folder
            FROM attachments a
            JOIN messages m ON m.id = a.message_id
            WHERE a.extraction_status = $noText
              AND {PdfOcrMatch}
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
        // Only move a still-'unsupported' row (guards against rowid reuse — see
        // SaveOcrText). A row that changed underneath us shouldn't be retyped.
        cmd.CommandText = "UPDATE attachments SET extraction_status = $noText WHERE id = $id AND extraction_status = $unsupported;";
        cmd.Parameters.AddWithValue("$noText", AttachmentTextExtractor.StatusNoText);
        cmd.Parameters.AddWithValue("$unsupported", AttachmentTextExtractor.StatusUnsupported);
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
                 WHERE a.extraction_status = $noText AND {PdfOcrMatch}
                   AND m.deleted_at IS NULL),
              (SELECT COUNT(*) FROM attachments a JOIN messages m ON m.id = a.message_id
                 WHERE a.extraction_status = $unsupported AND {ImageOcrMatch}
                   AND a.size_bytes >= $minBytes
                   AND m.deleted_at IS NULL),
              -- COALESCE the image predicate to 0: for an 'ocr' row with NULL
              -- content_type and a non-image filename it evaluates to NULL, and
              -- both `AND NOT NULL` and `AND NULL` are NULL, so the row would
              -- fall into neither recovered bucket. Treat it as non-image (PDF).
              (SELECT COUNT(*) FROM attachments a JOIN messages m ON m.id = a.message_id
                 WHERE a.extraction_status = $ocr AND NOT COALESCE(({ImageOcrMatch}), 0)
                   AND m.deleted_at IS NULL),
              (SELECT COUNT(*) FROM attachments a JOIN messages m ON m.id = a.message_id
                 WHERE a.extraction_status = $ocr AND COALESCE(({ImageOcrMatch}), 0)
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

        int updated;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            // Guard on the candidate statuses. attachments.id is a rowid without
            // AUTOINCREMENT, so a concurrent indexer content-change that
            // DELETE+INSERTs the row can reuse this id for a *different*
            // attachment between candidate selection and this write. Stamping
            // OCR text onto a row that's no longer a pending OCR candidate would
            // marry the old bytes' transcription to the new attachment (and mark
            // it searchable). If the row moved on, write nothing.
            cmd.CommandText = """
                UPDATE attachments
                SET extracted_text = $text, extraction_status = $ocr, extracted_at = $now
                WHERE id = $id AND extraction_status IN ($noText, $unsupported);
                """;
            cmd.Parameters.AddWithValue("$text", text);
            cmd.Parameters.AddWithValue("$ocr", AttachmentTextExtractor.StatusOcr);
            cmd.Parameters.AddWithValue("$noText", AttachmentTextExtractor.StatusNoText);
            cmd.Parameters.AddWithValue("$unsupported", AttachmentTextExtractor.StatusUnsupported);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", attachmentId);
            updated = cmd.ExecuteNonQuery();
        }

        if (updated == 0)
        {
            // Row was replaced/reprocessed since selection — don't rewrite the
            // message's FTS/embedding state off a stale assumption.
            tx.Rollback();
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // A blank transcription (empty/illegible scan). status='ocr' with
            // empty text is the terminal marker — the OCR pass won't
            // re-select it — but there is nothing new to search, so skip the
            // attachment_text rebuild and the embedded_at clear: re-queueing
            // would burn a full re-embed of the message for zero new content.
            tx.Commit();
            return;
        }

        var attachmentText = ConcatAttachmentText(conn, tx, messageId);
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE messages SET attachment_text = $at, embedded_at = NULL, embed_epoch = embed_epoch + 1 WHERE id = $id;";
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
                + (anyText ? ", embedded_at = NULL, embed_epoch = embed_epoch + 1" : "")
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
        // Guard on candidate statuses (rowid reuse — see SaveOcrText); only a
        // row still pending OCR should be retired to 'failed'.
        cmd.CommandText = "UPDATE attachments SET extraction_status = $failed WHERE id = $id AND extraction_status IN ($noText, $unsupported);";
        cmd.Parameters.AddWithValue("$failed", AttachmentTextExtractor.StatusFailed);
        cmd.Parameters.AddWithValue("$noText", AttachmentTextExtractor.StatusNoText);
        cmd.Parameters.AddWithValue("$unsupported", AttachmentTextExtractor.StatusUnsupported);
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

    /// <summary>
    /// Repoint a message's Maildir location without touching its content,
    /// hashes, or embedding state. Used by the scanner's reconciliation when
    /// a duplicate copy the row referenced is deleted: the surviving copy
    /// rides the mtime fast-path and never re-upserts, so without this the
    /// row's path would dangle forever — view_attachment fails and the OCR
    /// pass skips the message's attachments on every cycle.
    /// </summary>
    public void UpdateMaildirLocation(long id, string folder, string maildirRelativePath, string maildirFilename)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE messages
            SET folder = $folder, maildir_path = $path, maildir_filename = $file
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$folder", folder);
        cmd.Parameters.AddWithValue("$path", maildirRelativePath);
        cmd.Parameters.AddWithValue("$file", maildirFilename);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
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

    /// <summary>
    /// Counts soft-deleted messages; with <paramref name="deletedBefore"/>,
    /// only those whose deleted_at is at or before the cutoff (matching what
    /// <see cref="PurgeSoftDeleted"/> would remove with the same cutoff).
    /// </summary>
    public int CountSoftDeleted(DateTimeOffset? deletedBefore = null)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE deleted_at IS NOT NULL"
            + (deletedBefore is null ? "" : " AND datetime(deleted_at) <= datetime($cutoff)");
        if (deletedBefore is { } cutoff)
            cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
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
    ///
    /// <paramref name="deletedBefore"/> restricts the purge to rows whose
    /// deleted_at is at or before the cutoff. This grace period is what makes
    /// purge safe against the scanner's transient-failure window: a live
    /// message wrongly soft-deleted (its sync_state refresh failed mid-scan)
    /// self-heals on the next scan, but a purge landing inside that window
    /// would hard-delete it. Null purges everything regardless of age.
    /// </summary>
    public int PurgeSoftDeleted(DateTimeOffset? deletedBefore = null)
    {
        var cutoffClause = deletedBefore is null ? "" : " AND datetime(m.deleted_at) <= datetime($cutoff)";

        using var conn = connections.Open();
        using var tx = conn.BeginTransaction();

        using (var delEmbeddings = conn.CreateCommand())
        {
            delEmbeddings.Transaction = tx;
            delEmbeddings.CommandText = $"""
                DELETE FROM chunk_embeddings
                WHERE chunk_id IN (
                    SELECT c.id FROM chunks c
                    JOIN messages m ON m.id = c.message_id
                    WHERE m.deleted_at IS NOT NULL{cutoffClause}
                )
                """;
            if (deletedBefore is { } embCutoff)
                delEmbeddings.Parameters.AddWithValue("$cutoff", embCutoff.ToString("O"));
            delEmbeddings.ExecuteNonQuery();
        }

        int affected;
        using (var delMessages = conn.CreateCommand())
        {
            delMessages.Transaction = tx;
            delMessages.CommandText =
                "DELETE FROM messages AS m WHERE m.deleted_at IS NOT NULL" + cutoffClause;
            if (deletedBefore is { } msgCutoff)
                delMessages.Parameters.AddWithValue("$cutoff", msgCutoff.ToString("O"));
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
    IReadOnlyList<AttachmentEmbeddingPayload> Attachments,
    // content_hash at the moment the message was snapshotted for embedding.
    // The embedder passes it back to ReplaceChunksForMessage's guard so a
    // concurrent body change (which bumps content_hash) discards the stale
    // embed instead of stamping it. Nullable for legacy pre-v3 rows.
    string? ContentHash = null,
    // embed_epoch at snapshot time — the re-queue counter that catches
    // invalidations content_hash can't see (attachment text changes).
    long EmbedEpoch = 0);

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
