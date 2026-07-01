using Mailvec.Cli.Commands;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Mailvec.Cli.Tests;

public class BackfillInlineImagesCommandTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _maildirRoot;

    public BackfillInlineImagesCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mailvec-backfill-tests-" + Guid.NewGuid().ToString("N"));
        _maildirRoot = Path.Combine(_root, "Mail");
        _dbPath = Path.Combine(_root, "archive.sqlite");
        Directory.CreateDirectory(Path.Combine(_maildirRoot, "INBOX", "cur"));
    }

    public void Dispose()
    {
        var connections = new ConnectionFactory(Options.Create(new ArchiveOptions { DatabasePath = _dbPath }));
        using (var conn = connections.Open())
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(conn);
        }
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ArchiveOptions>(o => o.DatabasePath = _dbPath);
        services.Configure<IngestOptions>(o => o.MaildirRoot = _maildirRoot);
        services.AddSingleton<ConnectionFactory>();
        services.AddSingleton<SchemaMigrator>();
        services.AddSingleton<MessageRepository>();
        services.AddSingleton<AttachmentTextExtractor>();
        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<SchemaMigrator>().EnsureUpToDate();
        return sp;
    }

    // A message the indexer would have recorded with has_attachments=0: its HTML
    // references a cid: image, but the inline part never got an attachment row.
    // The .eml on disk still contains the inline image for the backfill to find.
    private long StageInlineOnlyMessage(IServiceProvider sp, string id)
    {
        var eml =
            "Message-ID: <" + id + ">\nFrom: a@x\nTo: b@x\nSubject: s\nMIME-Version: 1.0\n" +
            "Content-Type: multipart/related; boundary=\"rel\"\n\n" +
            "--rel\nContent-Type: text/html; charset=utf-8\n\n<div><img src=\"cid:img1\"></div>\n" +
            "--rel\nContent-Type: image/png; name=\"inline.png\"\n" +
            "Content-Disposition: inline; filename=\"inline.png\"\nContent-Transfer-Encoding: base64\nContent-ID: <img1>\n\n" +
            "SU1HREFUQQ==\n--rel--\n";
        File.WriteAllText(Path.Combine(_maildirRoot, "INBOX", "cur", id + ".eml"), eml);

        var repo = sp.GetRequiredService<MessageRepository>();
        var parsed = new ParsedMessage(
            MessageId: id, ThreadId: id, Subject: "s", FromAddress: "a@x", FromName: null,
            ToAddresses: [], CcAddresses: [], DateSent: DateTimeOffset.UtcNow, BodyText: "body",
            BodyHtml: "<div><img src=\"cid:img1\"></div>", RawHeaders: $"Message-ID: <{id}>\r\n",
            SizeBytes: 100, ContentHash: $"h-{id}", Attachments: []); // no attachment rows — the cid:-only shape
        return repo.Upsert(parsed, "INBOX", "INBOX/cur", id + ".eml", DateTimeOffset.UtcNow);
    }

    private static int Run(IServiceProvider sp, bool dryRun = false, long? messageId = null) =>
        BackfillInlineImagesCommand.Execute(sp, limit: null, batch: 100, dryRun: dryRun, messageId: messageId,
            new StringWriter(), new StringWriter());

    [Fact]
    public void No_cid_messages_reports_nothing_to_do()
    {
        using var sp = BuildProvider();
        var writer = new StringWriter();
        BackfillInlineImagesCommand.Execute(sp, null, 100, false, null, writer, new StringWriter());
        writer.ToString().ShouldContain("No messages reference inline");
    }

    [Fact]
    public void Backfill_adds_the_missing_inline_image_row()
    {
        using var sp = BuildProvider();
        long id = StageInlineOnlyMessage(sp, "inline@x");
        var repo = sp.GetRequiredService<MessageRepository>();
        repo.GetById(id)!.Attachments.ShouldBeEmpty(); // indexer skipped the inline image

        Run(sp).ShouldBe(0);

        var att = repo.GetById(id)!.Attachments.ShouldHaveSingleItem();
        att.PartIndex.ShouldBe(0);
        att.ContentType.ShouldBe("image/png");
        att.ExtractionStatus.ShouldBe(AttachmentTextExtractor.StatusUnsupported); // → picked up by the OCR pass
    }

    [Fact]
    public void Dry_run_writes_no_rows()
    {
        using var sp = BuildProvider();
        long id = StageInlineOnlyMessage(sp, "inline@x");

        Run(sp, dryRun: true);

        sp.GetRequiredService<MessageRepository>().GetById(id)!.Attachments.ShouldBeEmpty();
    }

    [Fact]
    public void Second_run_is_idempotent()
    {
        using var sp = BuildProvider();
        long id = StageInlineOnlyMessage(sp, "inline@x");
        var repo = sp.GetRequiredService<MessageRepository>();

        Run(sp);
        Run(sp); // the row now exists → GetAttachmentPartIndexes reports index 0 → nothing to add

        repo.GetById(id)!.Attachments.Count.ShouldBe(1);
    }

    [Fact]
    public void Single_message_mode_targets_just_that_message()
    {
        using var sp = BuildProvider();
        long a = StageInlineOnlyMessage(sp, "a@x");
        long b = StageInlineOnlyMessage(sp, "b@x");
        var repo = sp.GetRequiredService<MessageRepository>();

        Run(sp, messageId: a);

        repo.GetById(a)!.Attachments.Count.ShouldBe(1); // targeted
        repo.GetById(b)!.Attachments.ShouldBeEmpty();   // untouched
    }
}
