using Mailvec.Cli.Commands;
using Mailvec.Core.Data;
using Mailvec.Core.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace Mailvec.Cli.Tests;

public class PurgeDeletedCommandTests
{
    [Fact]
    public void Reports_nothing_to_do_when_no_soft_deleted_rows()
    {
        using var ctx = new TestServiceProvider();
        var writer = new StringWriter();

        var exit = PurgeDeletedCommand.Execute(ctx.Services, yes: true, dryRun: false, writer, readLine: () => null);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("No soft-deleted");
    }

    [Fact]
    public void Dry_run_reports_count_without_purging()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);
        messages.MarkDeleted([id], DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var exit = PurgeDeletedCommand.Execute(ctx.Services, yes: false, dryRun: true, writer, readLine: () => null);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("Dry run");
        // The soft-deleted row is still present.
        messages.CountSoftDeleted().ShouldBe(1);
    }

    [Fact]
    public void Aborts_when_user_answers_no_to_prompt()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);
        messages.MarkDeleted([id], DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var exit = PurgeDeletedCommand.Execute(ctx.Services, yes: false, dryRun: false, writer, readLine: () => "n");

        exit.ShouldBe(1);
        writer.ToString().ShouldContain("Aborted");
        messages.CountSoftDeleted().ShouldBe(1);
    }

    [Fact]
    public void Aborts_when_user_presses_enter_at_prompt()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);
        messages.MarkDeleted([id], DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var exit = PurgeDeletedCommand.Execute(ctx.Services, yes: false, dryRun: false, writer, readLine: () => "");

        exit.ShouldBe(1);
        messages.CountSoftDeleted().ShouldBe(1);
    }

    [Fact]
    public void Proceeds_when_yes_flag_is_set_and_purges_the_soft_deleted_rows()
    {
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        long keep = messages.Upsert(Sample("keep@x"), "INBOX", "INBOX/cur", "k", DateTimeOffset.UtcNow);
        long drop = messages.Upsert(Sample("drop@x"), "INBOX", "INBOX/cur", "d", DateTimeOffset.UtcNow);
        messages.MarkDeleted([drop], DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var exit = PurgeDeletedCommand.Execute(ctx.Services, yes: true, dryRun: false, writer, readLine: () => null);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("Purged 1");
        messages.CountSoftDeleted().ShouldBe(0);
        messages.CountAll().ShouldBe(1);                                          // only keep remains
        messages.GetByMessageId("keep@x").ShouldNotBeNull();
        messages.GetByMessageId("drop@x").ShouldBeNull();
    }

    [Fact]
    public void Proceeds_when_user_answers_y_at_prompt()
    {
        // Tests the case-insensitive "y" / "Y" handling and surrounding whitespace.
        using var ctx = new TestServiceProvider();
        var messages = ctx.Services.GetRequiredService<MessageRepository>();
        long id = messages.Upsert(Sample("a@x"), "INBOX", "INBOX/cur", "a", DateTimeOffset.UtcNow);
        messages.MarkDeleted([id], DateTimeOffset.UtcNow);

        var writer = new StringWriter();
        var exit = PurgeDeletedCommand.Execute(ctx.Services, yes: false, dryRun: false, writer, readLine: () => " Y ");

        exit.ShouldBe(0);
        messages.CountSoftDeleted().ShouldBe(0);
    }

    private static ParsedMessage Sample(string id) => new(
        MessageId: id,
        ThreadId: id,
        Subject: id,
        FromAddress: "alice@example.com",
        FromName: null,
        ToAddresses: [],
        CcAddresses: [],
        DateSent: DateTimeOffset.UtcNow,
        BodyText: "body",
        BodyHtml: null,
        RawHeaders: $"Message-ID: <{id}>\r\n",
        SizeBytes: 100,
        ContentHash: $"hash-{id}",
        Attachments: []);
}
