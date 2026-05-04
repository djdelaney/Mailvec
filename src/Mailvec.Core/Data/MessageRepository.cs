using System.Text;
using System.Text.Json;
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
public readonly record struct UpsertOutcome(long Id, bool ContentChanged)
{
    public static implicit operator long(UpsertOutcome o) => o.Id;
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

        long id;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO messages (
                    message_id, thread_id, maildir_path, maildir_filename, folder,
                    subject, from_address, from_name, to_addresses, cc_addresses,
                    date_sent, date_received, size_bytes, has_attachments, attachment_names,
                    body_text, body_html, raw_headers, indexed_at, deleted_at, content_hash
                ) VALUES (
                    $message_id, $thread_id, $maildir_path, $maildir_filename, $folder,
                    $subject, $from_address, $from_name, $to_addresses, $cc_addresses,
                    $date_sent, $date_received, $size_bytes, $has_attachments, $attachment_names,
                    $body_text, $body_html, $raw_headers, $indexed_at, NULL, $content_hash
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
                    body_text        = excluded.body_text,
                    body_html        = excluded.body_html,
                    raw_headers      = excluded.raw_headers,
                    indexed_at       = excluded.indexed_at,
                    deleted_at       = NULL,
                    content_hash     = excluded.content_hash
                RETURNING id;
                """;

            var attachmentNames = BuildAttachmentNames(parsed.Attachments);

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
            cmd.Parameters.AddWithValue("$body_text", (object?)parsed.BodyText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$body_html", (object?)parsed.BodyHtml ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$raw_headers", parsed.RawHeaders);
            cmd.Parameters.AddWithValue("$indexed_at", indexedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$content_hash", parsed.ContentHash);

            var idObj = cmd.ExecuteScalar()
                ?? throw new InvalidOperationException("Upsert returned no id");
            id = Convert.ToInt64(idObj, System.Globalization.CultureInfo.InvariantCulture);
        }

        ReplaceAttachments(conn, tx, id, parsed.Attachments);
        tx.Commit();
        return new UpsertOutcome(id, contentChanged);
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

    private static void ReplaceAttachments(SqliteConnection conn, SqliteTransaction tx, long messageId, IReadOnlyList<ParsedAttachment> attachments)
    {
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
            INSERT INTO attachments(message_id, part_index, filename, content_type, size_bytes)
            VALUES ($mid, $idx, $name, $ct, $size);
            """;
        var pMid = ins.Parameters.Add("$mid", SqliteType.Integer);
        var pIdx = ins.Parameters.Add("$idx", SqliteType.Integer);
        var pName = ins.Parameters.Add("$name", SqliteType.Text);
        var pCt = ins.Parameters.Add("$ct", SqliteType.Text);
        var pSize = ins.Parameters.Add("$size", SqliteType.Integer);
        pMid.Value = messageId;

        foreach (var a in attachments)
        {
            pIdx.Value = a.PartIndex;
            pName.Value = (object?)a.FileName ?? DBNull.Value;
            pCt.Value = (object?)a.ContentType ?? DBNull.Value;
            pSize.Value = (object?)a.SizeBytes ?? DBNull.Value;
            ins.ExecuteNonQuery();
        }
    }

    private IReadOnlyList<Attachment> GetAttachmentsForMessage(SqliteConnection conn, long messageId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT part_index, filename, content_type, size_bytes
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
                PartIndex: reader.GetInt32(0),
                FileName: reader.IsDBNull(1) ? null : reader.GetString(1),
                ContentType: reader.IsDBNull(2) ? null : reader.GetString(2),
                SizeBytes: reader.IsDBNull(3) ? null : reader.GetInt64(3)));
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
    /// Lazily streams unembedded, undeleted messages for the embedder. Returns
    /// (id, body_text) tuples. Messages with no body text are still returned
    /// so the worker can stamp them with zero chunks instead of leaving them
    /// stuck forever (they'd be invisible to the worker but counted by status).
    /// </summary>
    public IEnumerable<(long Id, string BodyText, string? Subject, string? AttachmentNames)> EnumerateUnembedded(int batchSize = 50)
    {
        using var conn = connections.Open();
        using var cmd = conn.CreateCommand();
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
            yield return (
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3));
        }
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
