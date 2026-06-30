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
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, writer, err);

        exit.ShouldBe(2);
        err.ToString().ShouldContain("Maildir root not found");
    }

    [Fact]
    public void Empty_db_reports_nothing_to_do()
    {
        using var sp = BuildProvider(maildirRoot: _maildirRoot);

        var writer = new StringWriter();
        var err = new StringWriter();
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, writer, err);

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
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, writer, err);

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
        var exit = ExtractAttachmentsCommand.Execute(sp, limit: null, batch: 100, noReembed: false, writer, err);

        exit.ShouldBe(0);
        err.ToString().ShouldContain("source not found");

        using var conn = sp.GetRequiredService<ConnectionFactory>().Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT extraction_status FROM attachments WHERE message_id = (SELECT id FROM messages WHERE message_id='ghost@x')";
        var status = cmd.ExecuteScalar() as string;
        status.ShouldBe("failed");
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
