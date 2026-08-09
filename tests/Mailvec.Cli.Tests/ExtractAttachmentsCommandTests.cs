using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Sx = DocumentFormat.OpenXml.Spreadsheet;
using Mailvec.Cli.Commands;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailvec.Cli.Tests;

public class ExtractAttachmentsCommandTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _maildirRoot;

    public ExtractAttachmentsCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mailvec-extract-tests-" + Guid.NewGuid().ToString("N"));
        _maildirRoot = Path.Combine(_root, "Mail");
        _dbPath = Path.Combine(_root, "archive.sqlite");
        Directory.CreateDirectory(Path.Combine(_maildirRoot, "INBOX", "cur"));
    }

    public void Dispose()
    {
        // Scope the pool clear to THIS database (see TempDatabase) — a global
        // ClearAllPools() races with parallel test classes' in-use connections.
        // The pool key derives solely from DatabasePath, so a fresh
        // ConnectionFactory on _dbPath produces the same connection string.
        var connections = new ConnectionFactory(Options.Create(new ArchiveOptions { DatabasePath = _dbPath }));
        using (var conn = connections.Open())
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(conn);
        }
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void Missing_maildir_root_returns_exit_2_and_writes_to_stderr()
    {
        using var sp = BuildProvider(maildirRoot: Path.Combine(_root, "does-not-exist"));

        var writer = new StringWriter();
        var err = new StringWriter();
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: null, writer, err);

        exit.ShouldBe(2);
        err.ToString().ShouldContain("Maildir root not found");
    }

    [Fact]
    public void Empty_db_reports_nothing_to_do()
    {
        using var sp = BuildProvider(maildirRoot: _maildirRoot);

        var writer = new StringWriter();
        var err = new StringWriter();
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: null, writer, err);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("No attachments need extraction");
    }

    [Fact]
    public void Backfill_runs_extractor_and_stamps_status()
    {
        using var sp = BuildProvider(maildirRoot: _maildirRoot);

        // Stage a real .eml with a plain text attachment so the extractor can
        // produce 'done' status, then upsert a message + NULL-status
        // attachment row that points at it.
        var emlPath = Path.Combine(_maildirRoot, "INBOX", "cur", "1.eml");
        File.WriteAllText(emlPath, """
            Message-ID: <a@x>
            From: alice@example.com
            To: bob@example.com
            Subject: Test
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: text/plain

            Body.
            --b
            Content-Type: text/plain; name="notes.txt"
            Content-Disposition: attachment; filename="notes.txt"

            Quarterly review notes — Q3 results in.
            --b--
            """);

        var messages = sp.GetRequiredService<MessageRepository>();
        var parsed = new ParsedMessage(
            MessageId: "a@x", ThreadId: "a@x", Subject: "Test",
            FromAddress: "alice@example.com", FromName: null,
            ToAddresses: [], CcAddresses: [],
            DateSent: DateTimeOffset.UtcNow,
            BodyText: "Body.", BodyHtml: null,
            RawHeaders: "Message-ID: <a@x>\r\n",
            SizeBytes: 200, ContentHash: "h",
            // NULL extraction_status — backfill should pick it up. PartIndex
            // matches MimeKit's `mime.Attachments` enumeration order (0-based).
            Attachments: [new ParsedAttachment(0, "notes.txt", "text/plain", 50L, ExtractedText: null, ExtractionStatus: null)]);
        messages.Upsert(parsed, "INBOX", "INBOX/cur", "1.eml", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var err = new StringWriter();
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: null, writer, err);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("Backfill candidates: 1");
        writer.ToString().ShouldContain("Processed 1 message");
        writer.ToString().ShouldContain("done");

        // Attachment row got stamped.
        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT extraction_status, extracted_text FROM attachments WHERE message_id = (SELECT id FROM messages WHERE message_id='a@x')";
        using var reader = cmd.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldBe("done");
        reader.GetString(1).ShouldContain("Quarterly");
    }

    // ── Extraction must not hold SQLite's writer lock ────────────────────────

    [Fact]
    public void Extraction_runs_with_the_writer_lock_free()
    {
        // BeginTransaction() issues BEGIN IMMEDIATE (deferred defaults to
        // false), so a transaction opened around the extract loop holds
        // SQLite's single writer slot for the whole MIME decode + PdfPig /
        // OpenXml parse — blocking the indexer, the embedder, the OCR
        // write-back, every maintenance command and MCP startup migration for
        // as long as the document takes. That is exactly what this command
        // used to do, while carrying a comment claiming it didn't.
        //
        // The probing extractor tries to take the writer lock from an
        // independent connection while it is being called. If the backfill
        // holds it, that attempt waits out the busy timeout and throws — so a
        // regression makes this test slow AND red, not just red.
        using var sp = BuildProvider(maildirRoot: _maildirRoot, probeWriterLock: true);

        var emlPath = Path.Combine(_maildirRoot, "INBOX", "cur", "lock.eml");
        var mime = new MimeMessage { Subject = "lock" };
        mime.From.Add(new MailboxAddress("", "a@x"));
        mime.To.Add(new MailboxAddress("", "b@x"));
        mime.Headers.Add("Message-ID", "<lock@x>");
        var memo = new MimePart("text", "plain")
        {
            Content = new MimeContent(new MemoryStream("attachment body"u8.ToArray())),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "memo.txt" },
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        mime.Body = new Multipart("mixed") { new TextPart("plain") { Text = "See attached." }, memo };
        using (var fs = File.Create(emlPath)) mime.WriteTo(fs);

        var messages = sp.GetRequiredService<MessageRepository>();
        messages.Upsert(
            new ParsedMessage(
                MessageId: "lock@x", ThreadId: "lock@x", Subject: "lock",
                FromAddress: "a@x", FromName: null, ToAddresses: [], CcAddresses: [],
                DateSent: DateTimeOffset.UtcNow, BodyText: "See attached.", BodyHtml: null,
                RawHeaders: "Message-ID: <lock@x>\r\n", SizeBytes: 200, ContentHash: "h",
                Attachments: [new ParsedAttachment(0, "memo.txt", "text/plain", 15L, ExtractedText: null, ExtractionStatus: null)]),
            "INBOX", "INBOX/cur", "lock.eml", DateTimeOffset.UtcNow);

        ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: null, new StringWriter(), new StringWriter());

        var probe = (WriterLockProbingExtractor)sp.GetRequiredService<AttachmentTextExtractor>();
        probe.Calls.ShouldBe(1, "the extractor must actually have run, or this proves nothing");
        probe.WriterLockWasFree.ShouldBeTrue(
            "extract-attachments held SQLite's writer lock while parsing an attachment");

        // And the result still lands — moving the parse out of the transaction
        // must not cost the write.
        AttachmentCol(sp, "lock@x", "extraction_status").ShouldBe("done");
    }

    // ── Missing source ───────────────────────────────────────────────────────
    //
    // This replaces a test that asserted the opposite ("...stamps attachments
    // failed so we do not loop forever"). That behaviour was the bug: on an
    // ordinary mbsync rename the snapshot's path vanishes while the bytes live
    // on unchanged at the new path, and because the content is unchanged
    // MessageRepository.Upsert never calls ReplaceAttachments — so the 'failed'
    // stamps survive the rescan. The default candidate predicate is
    // `extraction_status IS NULL`, so nothing ever revisits them: permanently
    // unsearchable attachments, no error, nothing left to re-trigger.
    //
    // Re-attempting a genuinely-deleted message costs one File.Exists per run,
    // which is the correct trade. Don't "optimise" it back into a stamp.

    [Fact]
    public void Missing_eml_file_leaves_attachments_untouched_for_a_later_run()
    {
        using var sp = BuildProvider(maildirRoot: _maildirRoot);

        // Upsert claims attachments but never stage the .eml on disk.
        var messages = sp.GetRequiredService<MessageRepository>();
        var parsed = new ParsedMessage(
            MessageId: "ghost@x", ThreadId: "ghost@x", Subject: "ghost",
            FromAddress: "alice@example.com", FromName: null,
            ToAddresses: [], CcAddresses: [],
            DateSent: DateTimeOffset.UtcNow,
            BodyText: "", BodyHtml: null,
            RawHeaders: "Message-ID: <ghost@x>\r\n",
            SizeBytes: 100, ContentHash: "h",
            Attachments: [new ParsedAttachment(1, "missing.pdf", "application/pdf", 100L, ExtractedText: null, ExtractionStatus: null)]);
        messages.Upsert(parsed, "INBOX", "INBOX/cur", "ghost.eml", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var err = new StringWriter();
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: null, writer, err);

        exit.ShouldBe(0);
        err.ToString().ShouldContain("source not found");

        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT extraction_status FROM attachments WHERE message_id = (SELECT id FROM messages WHERE message_id='ghost@x')";
        // Still NULL — so it remains a candidate for the next run, which is
        // what makes a rename recoverable without manual database repair.
        cmd.ExecuteScalar().ShouldBe(DBNull.Value);
    }

    [Fact]
    public void A_recorded_location_outside_the_maildir_root_is_refused_not_read()
    {
        // This backfill reads the whole .eml rather than one part, so it can't
        // go through MaildirAttachmentReader.Read — which is exactly how it
        // came to build the path with a bare Path.Combine and open it with no
        // containment check at all, quietly exempting itself from the
        // "never read outside the Maildir root" invariant. It now shares the
        // guard. The planted file is real and readable, so only the guard can
        // be what stops this: a File.Exists-only check would sail through.
        using var sp = BuildProvider(maildirRoot: _maildirRoot);

        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.eml"),
            "Message-ID: <esc@x>\nFrom: a@x\nTo: b@x\nSubject: s\n\nplain body\n");

        var messages = sp.GetRequiredService<MessageRepository>();
        var parsed = new ParsedMessage(
            MessageId: "esc@x", ThreadId: "esc@x", Subject: "esc",
            FromAddress: "alice@example.com", FromName: null,
            ToAddresses: [], CcAddresses: [],
            DateSent: DateTimeOffset.UtcNow,
            BodyText: "", BodyHtml: null,
            RawHeaders: "Message-ID: <esc@x>\r\n",
            SizeBytes: 100, ContentHash: "h",
            Attachments: [new ParsedAttachment(1, "x.pdf", "application/pdf", 100L, ExtractedText: null, ExtractionStatus: null)]);
        // A traversal sequence in maildir_path — the shape the guard exists for
        // if a future writer ever lets one into the column.
        messages.Upsert(parsed, "INBOX", "../outside", "secret.eml", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var err = new StringWriter();
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: null, writer, err);

        exit.ShouldBe(0);
        err.ToString().ShouldContain("refusing to read");
        // Reported in the summary, and reported as the non-transient thing it
        // is — a bounded run must never read as full coverage.
        writer.ToString().ShouldContain("REFUSED 1");

        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT extraction_status FROM attachments WHERE message_id = (SELECT id FROM messages WHERE message_id='esc@x')";
        cmd.ExecuteScalar().ShouldBe(DBNull.Value);
    }

    [Fact]
    public void One_refused_path_does_not_abort_the_rest_of_the_run()
    {
        // Per-message refusal, not fatal: a single poisoned row must not strand
        // the thousands of good messages behind it in the cursor.
        using var sp = BuildProvider(maildirRoot: _maildirRoot);

        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.eml"), "Message-ID: <esc2@x>\n\nbody\n");

        var messages = sp.GetRequiredService<MessageRepository>();
        var bad = new ParsedMessage(
            MessageId: "esc2@x", ThreadId: "esc2@x", Subject: "esc", FromAddress: "a@x", FromName: null,
            ToAddresses: [], CcAddresses: [], DateSent: DateTimeOffset.UtcNow,
            BodyText: "", BodyHtml: null, RawHeaders: "Message-ID: <esc2@x>\r\n",
            SizeBytes: 100, ContentHash: "hb",
            Attachments: [new ParsedAttachment(1, "x.pdf", "application/pdf", 100L, null, null)]);
        messages.Upsert(bad, "INBOX", "../outside", "secret.eml", DateTimeOffset.UtcNow);

        // A well-formed message that sorts after it by id.
        var emlPath = Path.Combine(_maildirRoot, "INBOX", "cur", "good.eml");
        File.WriteAllText(emlPath,
            "Message-ID: <good@x>\nFrom: a@x\nTo: b@x\nSubject: s\nMIME-Version: 1.0\n" +
            "Content-Type: multipart/mixed; boundary=\"b\"\n\n" +
            "--b\nContent-Type: text/plain\n\nbody\n" +
            "--b\nContent-Type: text/plain; name=\"note.txt\"\n" +
            "Content-Disposition: attachment; filename=\"note.txt\"\n\nhello from the attachment\n--b--\n");
        var good = new ParsedMessage(
            MessageId: "good@x", ThreadId: "good@x", Subject: "s", FromAddress: "a@x", FromName: null,
            ToAddresses: [], CcAddresses: [], DateSent: DateTimeOffset.UtcNow,
            BodyText: "body", BodyHtml: null, RawHeaders: "Message-ID: <good@x>\r\n",
            SizeBytes: 100, ContentHash: "hg",
            Attachments: [new ParsedAttachment(0, "note.txt", "text/plain", 25L, null, null)]);
        messages.Upsert(good, "INBOX", "INBOX/cur", "good.eml", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var err = new StringWriter();
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: null, writer, err);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("REFUSED 1");

        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT extraction_status FROM attachments WHERE message_id = (SELECT id FROM messages WHERE message_id='good@x')";
        cmd.ExecuteScalar().ShouldBe(AttachmentTextExtractor.StatusDone);
    }

    [Fact]
    public void A_renamed_message_is_reported_as_skipped_not_counted_as_covered()
    {
        // The run summary must never let a bounded run read as full coverage —
        // same rule the stale-skip count follows.
        using var sp = BuildProvider(maildirRoot: _maildirRoot);
        var messages = sp.GetRequiredService<MessageRepository>();
        var parsed = new ParsedMessage(
            MessageId: "moved@x", ThreadId: "moved@x", Subject: "moved",
            FromAddress: "alice@example.com", FromName: null,
            ToAddresses: [], CcAddresses: [],
            DateSent: DateTimeOffset.UtcNow,
            BodyText: "", BodyHtml: null,
            RawHeaders: "Message-ID: <moved@x>\r\n",
            SizeBytes: 100, ContentHash: "h",
            Attachments: [new ParsedAttachment(1, "moved.pdf", "application/pdf", 100L, ExtractedText: null, ExtractionStatus: null)]);
        messages.Upsert(parsed, "INBOX", "INBOX/cur", "moved.eml", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: null, writer, new StringWriter());

        writer.ToString().ShouldContain("Maildir source was missing");
    }

    [Fact]
    public void A_second_run_after_the_source_reappears_extracts_normally()
    {
        // The whole point of not stamping: recovery is automatic. First run
        // finds nothing on disk, second run — after the file turns up at the
        // path the row points at, as it would once the indexer reconciles a
        // rename — extracts as usual.
        using var sp = BuildProvider(maildirRoot: _maildirRoot);
        var messages = sp.GetRequiredService<MessageRepository>();
        var parsed = new ParsedMessage(
            MessageId: "late@x", ThreadId: "late@x", Subject: "late",
            FromAddress: "alice@example.com", FromName: null,
            ToAddresses: [], CcAddresses: [],
            DateSent: DateTimeOffset.UtcNow,
            BodyText: "", BodyHtml: null,
            RawHeaders: "Message-ID: <late@x>\r\n",
            SizeBytes: 100, ContentHash: "h",
            Attachments: [new ParsedAttachment(0, "late.txt", "text/plain", 20L, ExtractedText: null, ExtractionStatus: null)]);
        messages.Upsert(parsed, "INBOX", "INBOX/cur", "late.eml", DateTimeOffset.UtcNow);

        ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: null, new StringWriter(), new StringWriter());

        // Now stage the .eml where the row says it is — what an indexer
        // reconciliation of the rename amounts to from this command's side.
        var emlPath = Path.Combine(_maildirRoot, "INBOX", "cur", "late.eml");
        var mime = new MimeMessage { Subject = "late" };
        mime.From.Add(new MailboxAddress("", "alice@example.com"));
        mime.To.Add(new MailboxAddress("", "b@x"));
        mime.Headers.Add("Message-ID", "<late@x>");
        var memo = new MimePart("text", "plain")
        {
            Content = new MimeContent(new MemoryStream("recovered attachment text"u8.ToArray())),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "late.txt" },
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        mime.Body = new Multipart("mixed") { new TextPart("plain") { Text = "See attached." }, memo };
        using (var fs = File.Create(emlPath)) mime.WriteTo(fs);

        ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: null, new StringWriter(), new StringWriter());

        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT extraction_status, extracted_text FROM attachments WHERE message_id = (SELECT id FROM messages WHERE message_id='late@x')";
        using var reader = cmd.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldBe("done");
        reader.GetString(1).ShouldContain("recovered");
    }

    // ── Stale-parse guard ────────────────────────────────────────────────────
    //
    // The .eml is parsed OUTSIDE the write transaction on purpose (PdfPig on a
    // large document must not hold the writer lock), which leaves a window for
    // the indexer to re-parse the message and replace its attachment rows. The
    // parse in hand then describes a file the row no longer points at, and its
    // part_index-keyed results would be stamped onto the current rows.

    [Fact]
    public void A_message_that_changed_after_the_page_load_is_skipped_rather_than_stamped()
    {
        using var sp = BuildProvider(maildirRoot: _maildirRoot);
        var messages = sp.GetRequiredService<MessageRepository>();

        // Two candidates in one page, ordered by messages.id. The first has no
        // .eml on disk, so the command writes to stderr while working through
        // the page — the hook we use to mutate the SECOND message after its
        // snapshot was taken but before it is processed. That is exactly the
        // window a concurrent indexer lands in.
        messages.Upsert(SampleParsed("ghost@x", "missing.pdf", "application/pdf"), "INBOX", "INBOX/cur", "ghost.eml", DateTimeOffset.UtcNow);

        var emlPath = Path.Combine(_maildirRoot, "INBOX", "cur", "victim.eml");
        File.WriteAllText(emlPath, """
            Message-ID: <victim@x>
            From: alice@example.com
            To: bob@example.com
            Subject: Test
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: text/plain

            Body.
            --b
            Content-Type: text/plain; name="notes.txt"
            Content-Disposition: attachment; filename="notes.txt"

            ZQTELEMETRY from the pre-change parse.
            --b--
            """);
        messages.Upsert(SampleParsed("victim@x", "notes.txt", "text/plain"), "INBOX", "INBOX/cur", "victim.eml", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var err = new MutateOnFirstWrite(() => SetContentHash(sp, "victim@x", "h-changed-by-the-indexer"));
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: null, writer, err);

        exit.ShouldBe(0);
        // The row must be left alone — an unstamped candidate is picked up by
        // the next run against a fresh parse; a wrongly-stamped one never is.
        StatusOf(sp, "victim@x").ShouldBeNull();
        ExtractedTextOf(sp, "victim@x").ShouldBeNull();
        // ...and the run must say so rather than silently doing less.
        writer.ToString().ShouldContain("changed underneath");
    }

    [Fact]
    public void An_unchanged_message_is_not_treated_as_stale()
    {
        // Same shape as above minus the mutation: the guard must not reject the
        // ordinary case (notably a legacy NULL content_hash on both sides).
        using var sp = BuildProvider(maildirRoot: _maildirRoot);
        var messages = sp.GetRequiredService<MessageRepository>();

        var emlPath = Path.Combine(_maildirRoot, "INBOX", "cur", "ok.eml");
        File.WriteAllText(emlPath, """
            Message-ID: <ok@x>
            From: alice@example.com
            To: bob@example.com
            Subject: Test
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: text/plain

            Body.
            --b
            Content-Type: text/plain; name="notes.txt"
            Content-Disposition: attachment; filename="notes.txt"

            ZQTELEMETRY quarterly notes.
            --b--
            """);
        messages.Upsert(SampleParsed("ok@x", "notes.txt", "text/plain"), "INBOX", "INBOX/cur", "ok.eml", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: null, writer, new StringWriter());

        exit.ShouldBe(0);
        StatusOf(sp, "ok@x").ShouldBe("done");
        ExtractedTextOf(sp, "ok@x").ShouldNotBeNull().ShouldContain("ZQTELEMETRY");
        writer.ToString().ShouldNotContain("changed underneath");
    }

    /// <summary>Runs <paramref name="onFirstWrite"/> once, the first time anything is written.</summary>
    private sealed class MutateOnFirstWrite(Action onFirstWrite) : StringWriter
    {
        private bool _fired;

        public override void Write(string? value)
        {
            base.Write(value);
            if (_fired) return;
            _fired = true;
            onFirstWrite();
        }
    }

    private static ParsedMessage SampleParsed(string id, string attachmentName, string contentType) => new(
        MessageId: id, ThreadId: id, Subject: "Test",
        FromAddress: "alice@example.com", FromName: null,
        ToAddresses: [], CcAddresses: [],
        DateSent: DateTimeOffset.UtcNow,
        BodyText: "Body.", BodyHtml: null,
        RawHeaders: $"Message-ID: <{id}>\r\n",
        SizeBytes: 200, ContentHash: $"h-{id}",
        Attachments: [new ParsedAttachment(0, attachmentName, contentType, 50L, ExtractedText: null, ExtractionStatus: null)]);

    private static void SetContentHash(IServiceProvider sp, string messageIdHeader, string hash)
    {
        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE messages SET content_hash = $h WHERE message_id = $mid";
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$mid", messageIdHeader);
        cmd.ExecuteNonQuery();
    }

    private static string? StatusOf(IServiceProvider sp, string messageIdHeader) =>
        AttachmentCol(sp, messageIdHeader, "extraction_status");

    private static string? ExtractedTextOf(IServiceProvider sp, string messageIdHeader) =>
        AttachmentCol(sp, messageIdHeader, "extracted_text");

    private static string? AttachmentCol(IServiceProvider sp, string messageIdHeader, string column)
    {
        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM attachments WHERE message_id = (SELECT id FROM messages WHERE message_id = $mid)";
        cmd.Parameters.AddWithValue("$mid", messageIdHeader);
        return cmd.ExecuteScalar() as string;
    }

    private static object? MessageCol(IServiceProvider sp, string messageIdHeader, string column)
    {
        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM messages WHERE message_id = $mid";
        cmd.Parameters.AddWithValue("$mid", messageIdHeader);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    private static void StampEmbedded(IServiceProvider sp, string messageIdHeader)
    {
        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE messages SET embedded_at = $t WHERE message_id = $mid";
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$mid", messageIdHeader);
        cmd.ExecuteNonQuery();
    }

    // ── Re-queue must be atomic with the attachment-text write ───────────────
    //
    // These pin the fix for a permanent, silent divergence. The re-queue used
    // to run in a SEPARATE transaction after the text commit, and the inline
    // comment claimed a failure there would "retry on the next pass". It would
    // not: the default candidate predicate is `extraction_status IS NULL`, and
    // the committed transaction just made it non-null, so a re-run skips these
    // rows entirely. Nothing else re-triggers either — the embedder only takes
    // `embedded_at IS NULL`. The result was new attachment text in FTS beside
    // pre-extraction vectors, forever, with no error.
    //
    // embed_epoch matters as much as embedded_at: content_hash is untouched by
    // extraction, so the embedder's hash guard alone would let an in-flight
    // embed stamp straight over the re-queue.

    [Fact]
    public void Extraction_requeues_the_message_atomically_with_the_text()
    {
        using var sp = BuildProvider(maildirRoot: _maildirRoot);
        SeedTextAttachmentMessage(sp, "requeue@x", "requeue.eml");
        StampEmbedded(sp, "requeue@x");
        var epochBefore = Convert.ToInt64(MessageCol(sp, "requeue@x", "embed_epoch"), System.Globalization.CultureInfo.InvariantCulture);

        ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: null, new StringWriter(), new StringWriter());

        AttachmentCol(sp, "requeue@x", "extraction_status").ShouldBe("done");
        MessageCol(sp, "requeue@x", "attachment_text").ShouldNotBeNull();
        MessageCol(sp, "requeue@x", "embedded_at")
            .ShouldBeNull("extraction wrote new attachment text, so the message must be re-queued for embedding");
        // Greater-than, not +1: the in-transaction bump and the prompt
        // ClearEmbeddingsForMessage cleanup each bump once. Harmless — the
        // embedder's guard tests the epoch for *change*, not magnitude — and
        // asserting movement rather than an exact count keeps this test honest
        // if the cleanup call is ever dropped as the optimization it is.
        Convert.ToInt64(MessageCol(sp, "requeue@x", "embed_epoch"), System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBeGreaterThan(epochBefore, "embed_epoch must move or an in-flight embed can stamp over the re-queue");
    }

    [Fact]
    public void Extraction_with_no_reembed_leaves_the_embedding_state_alone()
    {
        using var sp = BuildProvider(maildirRoot: _maildirRoot);
        SeedTextAttachmentMessage(sp, "noreembed@x", "noreembed.eml");
        StampEmbedded(sp, "noreembed@x");
        var epochBefore = Convert.ToInt64(MessageCol(sp, "noreembed@x", "embed_epoch"), System.Globalization.CultureInfo.InvariantCulture);

        ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: true, reextractKind: null, new StringWriter(), new StringWriter());

        AttachmentCol(sp, "noreembed@x", "extraction_status").ShouldBe("done");
        MessageCol(sp, "noreembed@x", "embedded_at").ShouldNotBeNull("--no-reembed opts out of the re-queue");
        Convert.ToInt64(MessageCol(sp, "noreembed@x", "embed_epoch"), System.Globalization.CultureInfo.InvariantCulture).ShouldBe(epochBefore);
    }

    private void SeedTextAttachmentMessage(IServiceProvider sp, string messageIdHeader, string fileName)
    {
        var emlPath = Path.Combine(_maildirRoot, "INBOX", "cur", fileName);
        var mime = new MimeMessage { Subject = "s" };
        mime.From.Add(new MailboxAddress("", "a@x"));
        mime.To.Add(new MailboxAddress("", "b@x"));
        mime.Headers.Add("Message-ID", $"<{messageIdHeader}>");
        var memo = new MimePart("text", "plain")
        {
            Content = new MimeContent(new MemoryStream("attachment body"u8.ToArray())),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "memo.txt" },
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        mime.Body = new Multipart("mixed") { new TextPart("plain") { Text = "See attached." }, memo };
        using (var fs = File.Create(emlPath)) mime.WriteTo(fs);

        sp.GetRequiredService<MessageRepository>().Upsert(
            new ParsedMessage(
                MessageId: messageIdHeader, ThreadId: messageIdHeader, Subject: "s",
                FromAddress: "a@x", FromName: null, ToAddresses: [], CcAddresses: [],
                DateSent: DateTimeOffset.UtcNow, BodyText: "See attached.", BodyHtml: null,
                RawHeaders: $"Message-ID: <{messageIdHeader}>\r\n", SizeBytes: 200, ContentHash: "h",
                Attachments: [new ParsedAttachment(0, "memo.txt", "text/plain", 15L, ExtractedText: null, ExtractionStatus: null)]),
            "INBOX", "INBOX/cur", fileName, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Reextract_calendar_recovers_ics_rows_and_leaves_others_untouched()
    {
        using var sp = BuildProvider(maildirRoot: _maildirRoot);

        // A message with two already-stamped 'unsupported' attachments: a
        // calendar invite (the routing fix should recover it) and a zip (the
        // calendar predicate must NOT touch it). ICS lines sit at the raw-string
        // baseline so none gets a leading space — a leading space is an RFC 5545
        // fold-continuation and would corrupt the fixture.
        var emlPath = Path.Combine(_maildirRoot, "INBOX", "cur", "cal.eml");
        File.WriteAllText(emlPath, """
            Message-ID: <cal@x>
            From: alice@example.com
            To: bob@example.com
            Subject: Invite
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: text/plain

            Body.
            --b
            Content-Type: text/calendar; name="invite.ics"
            Content-Disposition: attachment; filename="invite.ics"

            BEGIN:VCALENDAR
            VERSION:2.0
            BEGIN:VEVENT
            UID:evt-1@x
            DTSTAMP:20250101T000000Z
            SUMMARY:Team Offsite Planning
            LOCATION:Conference Room A
            END:VEVENT
            END:VCALENDAR
            --b
            Content-Type: application/zip; name="data.zip"
            Content-Disposition: attachment; filename="data.zip"
            Content-Transfer-Encoding: base64

            UEsDBAoAAAAAAA==
            --b--
            """);

        var messages = sp.GetRequiredService<MessageRepository>();
        var parsed = new ParsedMessage(
            MessageId: "cal@x", ThreadId: "cal@x", Subject: "Invite",
            FromAddress: "alice@example.com", FromName: null,
            ToAddresses: [], CcAddresses: [],
            DateSent: DateTimeOffset.UtcNow,
            BodyText: "Body.", BodyHtml: null,
            RawHeaders: "Message-ID: <cal@x>\r\n",
            SizeBytes: 300, ContentHash: "h",
            // PartIndex matches mime.Attachments order: calendar (0), zip (1).
            Attachments:
            [
                new ParsedAttachment(0, "invite.ics", "text/calendar", 120L, ExtractedText: null, ExtractionStatus: "unsupported"),
                new ParsedAttachment(1, "data.zip", "application/zip", 20L, ExtractedText: null, ExtractionStatus: "unsupported"),
            ]);
        messages.Upsert(parsed, "INBOX", "INBOX/cur", "cal.eml", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var err = new StringWriter();
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: "calendar", writer, err);

        exit.ShouldBe(0);

        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT filename, extraction_status, COALESCE(extracted_text,'') FROM attachments WHERE message_id=(SELECT id FROM messages WHERE message_id='cal@x') ORDER BY part_index";
        using var reader = cmd.ExecuteReader();

        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldBe("invite.ics");
        reader.GetString(1).ShouldBe("done");                        // recovered from 'unsupported'
        reader.GetString(2).ShouldContain("Team Offsite Planning");  // clean field extraction
        reader.GetString(2).ShouldContain("Location: Conference Room A");
        reader.GetString(2).ShouldNotContain("BEGIN:VCALENDAR");     // scaffolding gone

        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldBe("data.zip");
        reader.GetString(1).ShouldBe("unsupported");                 // NOT a calendar candidate
    }

    [Fact]
    public void Default_mode_does_not_touch_already_stamped_calendar_rows()
    {
        using var sp = BuildProvider(maildirRoot: _maildirRoot);

        var emlPath = Path.Combine(_maildirRoot, "INBOX", "cur", "cal2.eml");
        File.WriteAllText(emlPath, """
            Message-ID: <cal2@x>
            From: alice@example.com
            Subject: Invite
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: text/plain

            Body.
            --b
            Content-Type: text/calendar; name="invite.ics"
            Content-Disposition: attachment; filename="invite.ics"

            BEGIN:VCALENDAR
            BEGIN:VEVENT
            SUMMARY:Team Offsite Planning
            END:VEVENT
            END:VCALENDAR
            --b--
            """);

        var messages = sp.GetRequiredService<MessageRepository>();
        var parsed = new ParsedMessage(
            MessageId: "cal2@x", ThreadId: "cal2@x", Subject: "Invite",
            FromAddress: "alice@example.com", FromName: null,
            ToAddresses: [], CcAddresses: [],
            DateSent: DateTimeOffset.UtcNow,
            BodyText: "Body.", BodyHtml: null,
            RawHeaders: "Message-ID: <cal2@x>\r\n",
            SizeBytes: 200, ContentHash: "h",
            Attachments: [new ParsedAttachment(0, "invite.ics", "text/calendar", 80L, ExtractedText: null, ExtractionStatus: "unsupported")]);
        messages.Upsert(parsed, "INBOX", "INBOX/cur", "cal2.eml", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var err = new StringWriter();
        // Default (NULL-only) mode: an already-stamped 'unsupported' row is not a
        // candidate — only --reextract-calendar reaches it.
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: null, writer, err);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("No attachments need extraction");

        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT extraction_status FROM attachments WHERE message_id=(SELECT id FROM messages WHERE message_id='cal2@x')";
        (cmd.ExecuteScalar() as string).ShouldBe("unsupported");
    }

    [Fact]
    public void Reextract_vcard_recovers_octet_stream_vcf_rows()
    {
        using var sp = BuildProvider(maildirRoot: _maildirRoot);

        var emlPath = Path.Combine(_maildirRoot, "INBOX", "cur", "card.eml");
        File.WriteAllText(emlPath, """
            Message-ID: <card@x>
            From: alice@example.com
            Subject: Contact
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: text/plain

            Body.
            --b
            Content-Type: application/octet-stream; name="jane.vcf"
            Content-Disposition: attachment; filename="jane.vcf"

            BEGIN:VCARD
            VERSION:3.0
            FN:Jane Roe
            ORG:Acme Corp
            TITLE:Engineer
            EMAIL:jane@acme.example
            TEL:+1-555-0100
            END:VCARD
            --b--
            """);

        var messages = sp.GetRequiredService<MessageRepository>();
        var parsed = new ParsedMessage(
            MessageId: "card@x", ThreadId: "card@x", Subject: "Contact",
            FromAddress: "alice@example.com", FromName: null,
            ToAddresses: [], CcAddresses: [],
            DateSent: DateTimeOffset.UtcNow,
            BodyText: "Body.", BodyHtml: null,
            RawHeaders: "Message-ID: <card@x>\r\n",
            SizeBytes: 200, ContentHash: "h",
            // Mislabeled octet-stream .vcf, previously stamped 'unsupported'.
            Attachments: [new ParsedAttachment(0, "jane.vcf", "application/octet-stream", 100L, ExtractedText: null, ExtractionStatus: "unsupported")]);
        messages.Upsert(parsed, "INBOX", "INBOX/cur", "card.eml", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var err = new StringWriter();
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: "vcard", writer, err);

        exit.ShouldBe(0);

        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT extraction_status, COALESCE(extracted_text,'') FROM attachments WHERE message_id=(SELECT id FROM messages WHERE message_id='card@x')";
        using var reader = cmd.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldBe("done");             // recovered from 'unsupported'
        reader.GetString(1).ShouldContain("Jane Roe");
        reader.GetString(1).ShouldContain("Org: Acme Corp");
        reader.GetString(1).ShouldNotContain("BEGIN:VCARD");
    }

    [Fact]
    public void Reextract_office_recovers_unsupported_xlsx_rows()
    {
        using var sp = BuildProvider(maildirRoot: _maildirRoot);

        // Stage a real .eml carrying a real .xlsx (binary) attachment, built with
        // MimeKit so the part round-trips to attachment index 0.
        var emlPath = Path.Combine(_maildirRoot, "INBOX", "cur", "sheet.eml");
        var msg = new MimeMessage { Subject = "Spreadsheet" };
        msg.From.Add(new MailboxAddress("", "a@x"));
        msg.To.Add(new MailboxAddress("", "b@x"));
        msg.Headers.Add("Message-ID", "<sheet@x>");
        var xlsx = new MimePart("application", "vnd.openxmlformats-officedocument.spreadsheetml.sheet")
        {
            Content = new MimeContent(new MemoryStream(BuildXlsx("Guests", "Alice Johnson", "Table 4"))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "guests.xlsx" },
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        msg.Body = new Multipart("mixed") { new TextPart("plain") { Text = "See attached." }, xlsx };
        using (var fs = File.Create(emlPath)) msg.WriteTo(fs);

        var messages = sp.GetRequiredService<MessageRepository>();
        var parsed = new ParsedMessage(
            MessageId: "sheet@x", ThreadId: "sheet@x", Subject: "Spreadsheet",
            FromAddress: "a@x", FromName: null, ToAddresses: [], CcAddresses: [],
            DateSent: DateTimeOffset.UtcNow, BodyText: "See attached.", BodyHtml: null,
            RawHeaders: "Message-ID: <sheet@x>\r\n", SizeBytes: 400, ContentHash: "h",
            Attachments: [new ParsedAttachment(0, "guests.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 500L, ExtractedText: null, ExtractionStatus: "unsupported")]);
        messages.Upsert(parsed, "INBOX", "INBOX/cur", "sheet.eml", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var err = new StringWriter();
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: "office", writer, err);

        exit.ShouldBe(0);

        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT extraction_status, COALESCE(extracted_text,'') FROM attachments WHERE message_id=(SELECT id FROM messages WHERE message_id='sheet@x')";
        using var reader = cmd.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldBe("done");             // recovered from 'unsupported'
        reader.GetString(1).ShouldContain("Alice Johnson");
        reader.GetString(1).ShouldContain("Guests");      // sheet name
    }

    [Fact]
    public void Reextract_text_recovers_mojibake_via_declared_charset()
    {
        using var sp = BuildProvider(maildirRoot: _maildirRoot);

        // A Shift-JIS text attachment whose row was stamped 'done' with
        // mojibake by the old UTF-8→Windows-1252 ladder. --reextract-text is
        // the backfill: re-runs the extractor, which now honors the declared
        // charset.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var sjis = System.Text.Encoding.GetEncoding("shift_jis").GetBytes("請求書を添付します Invoice attached");
        var emlPath = Path.Combine(_maildirRoot, "INBOX", "cur", "sjis.eml");
        var msg = new MimeMessage { Subject = "txt" };
        msg.From.Add(new MailboxAddress("", "a@x"));
        msg.To.Add(new MailboxAddress("", "b@x"));
        msg.Headers.Add("Message-ID", "<sjis@x>");
        var memo = new MimePart("text", "plain")
        {
            Content = new MimeContent(new MemoryStream(sjis)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "memo.txt" },
            ContentTransferEncoding = ContentEncoding.Base64,
        };
        memo.ContentType.Charset = "shift_jis";
        msg.Body = new Multipart("mixed") { new TextPart("plain") { Text = "See attached." }, memo };
        using (var fs = File.Create(emlPath)) msg.WriteTo(fs);

        var messages = sp.GetRequiredService<MessageRepository>();
        var parsed = new ParsedMessage(
            MessageId: "sjis@x", ThreadId: "sjis@x", Subject: "txt",
            FromAddress: "a@x", FromName: null, ToAddresses: [], CcAddresses: [],
            DateSent: DateTimeOffset.UtcNow, BodyText: "See attached.", BodyHtml: null,
            RawHeaders: "Message-ID: <sjis@x>\r\n", SizeBytes: 200, ContentHash: "h",
            Attachments: [new ParsedAttachment(0, "memo.txt", "text/plain", sjis.LongLength,
                ExtractedText: "ﾀｸﾋﾟ mojibake residue", ExtractionStatus: "done")]);
        messages.Upsert(parsed, "INBOX", "INBOX/cur", "sjis.eml", DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var err = new StringWriter();
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, reextractKind: "text", writer, err);

        exit.ShouldBe(0);

        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT extraction_status, COALESCE(extracted_text,'') FROM attachments WHERE message_id=(SELECT id FROM messages WHERE message_id='sjis@x')";
        using var reader = cmd.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldBe("done");
        reader.GetString(1).ShouldContain("請求書を添付します");   // real text, not mojibake
        reader.GetString(1).ShouldNotContain("mojibake residue");
    }

    private static byte[] BuildXlsx(string sheetName, params string[] cellTexts)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new Sx.Workbook();
            var sheets = wbPart.Workbook.AppendChild(new Sx.Sheets());
            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            wsPart.Worksheet = new Sx.Worksheet(new Sx.SheetData());
            var sstPart = wbPart.AddNewPart<SharedStringTablePart>();
            sstPart.SharedStringTable = new Sx.SharedStringTable();
            foreach (var t in cellTexts)
            {
                sstPart.SharedStringTable.AppendChild(new Sx.SharedStringItem(new Sx.Text(t)));
            }
            sheets.AppendChild(new Sx.Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = 1, Name = sheetName });
        }
        return ms.ToArray();
    }

    private ServiceProvider BuildProvider(string maildirRoot, bool probeWriterLock = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ArchiveOptions>(o => o.DatabasePath = _dbPath);
        services.Configure<IngestOptions>(o => o.MaildirRoot = maildirRoot);
        services.Configure<McpOptions>(_ => { });
        services.AddSingleton<ConnectionFactory>();
        services.AddSingleton<SchemaMigrator>();
        services.AddSingleton<MessageRepository>();
        services.AddSingleton<MetadataRepository>();
        services.AddSingleton<ChunkRepository>();
        if (probeWriterLock)
            services.AddSingleton<AttachmentTextExtractor, WriterLockProbingExtractor>();
        else
            services.AddSingleton<AttachmentTextExtractor>();
        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        return sp;
    }

    /// <summary>
    /// Extractor that, from inside Extract, tries to take SQLite's writer lock
    /// on an INDEPENDENT connection. Succeeding means the backfill was not
    /// holding it while parsing — which is the invariant under test.
    /// </summary>
    private sealed class WriterLockProbingExtractor(
        ConnectionFactory connections,
        IOptions<IndexerOptions> indexerOptions,
        ILogger<AttachmentTextExtractor> logger)
        : AttachmentTextExtractor(indexerOptions, logger)
    {
        public int Calls { get; private set; }
        public bool WriterLockWasFree { get; private set; } = true;

        public override ExtractionResult Extract(MimeEntity entity, string? fileName, string? contentType, long? declaredSize)
        {
            Calls++;
            try
            {
                using var conn = connections.Open();
                // BEGIN IMMEDIATE — takes the writer lock outright.
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE metadata SET value = value WHERE key = 'schema_version'";
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // Blocked out by the backfill's own transaction — the bug.
                WriterLockWasFree = false;
            }
            return base.Extract(entity, fileName, contentType, declaredSize);
        }
    }
}
