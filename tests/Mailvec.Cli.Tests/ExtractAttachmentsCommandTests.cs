using Mailvec.Cli.Commands;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

    [Fact]
    public void Missing_eml_file_stamps_attachments_failed_so_we_do_not_loop_forever()
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
        var status = cmd.ExecuteScalar() as string;
        status.ShouldBe("failed");
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

    private ServiceProvider BuildProvider(string maildirRoot)
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
        services.AddSingleton<AttachmentTextExtractor>();
        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        return sp;
    }
}
