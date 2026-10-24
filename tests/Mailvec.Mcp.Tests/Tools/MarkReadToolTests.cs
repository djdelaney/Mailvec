using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Mcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace Mailvec.Mcp.Tests.Tools;

public sealed class MarkReadToolTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly string _root;
    private readonly string _queue;

    public MarkReadToolTests()
    {
        _root = Path.Combine(_db.DirectoryPath, "mail", "Fastmail");
        _queue = Path.Combine(_db.DirectoryPath, "mark-read");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_queue);
    }

    public void Dispose() => _db.Dispose();

    private MarkReadTool Build(int maxPerCall = 100)
    {
        var mcp = Options.Create(new McpOptions
        {
            MarkRead = new MarkReadOptions { QueueDirectory = _queue, MaxMessagesPerCall = maxPerCall },
        });
        var ingest = Options.Create(new IngestOptions { MaildirRoot = _root });
        return new MarkReadTool(
            new MessageRepository(_db.Connections),
            new SyncStateRepository(_db.Connections),
            new MarkReadQueue(mcp, ingest),
            mcp,
            Helpers.NoopLogger(),
            NullLogger<MarkReadTool>.Instance);
    }

    private void Applier(DateTimeOffset at, int cadence = 60, params string[] folders) =>
        File.WriteAllLines(Path.Combine(_queue, MarkReadQueue.ApplierFileName),
            [MarkReadQueue.ApplierHeader, at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"), cadence.ToString(), .. folders]);

    /// <summary>One message with one tracked copy per (folder, filename).</summary>
    private long Seed(string messageId, params (string Folder, string FileName)[] copies)
    {
        var id = new MessageRepository(_db.Connections).Upsert(
            Helpers.Sample(messageId), copies[0].Folder, $"{copies[0].Folder}/cur", copies[0].FileName, DateTimeOffset.UtcNow).Id;
        var sync = new SyncStateRepository(_db.Connections);
        using var conn = _db.Connections.Open();
        using var tx = conn.BeginTransaction();
        foreach (var (folder, file) in copies)
        {
            var leaf = file.Contains(":2,", StringComparison.Ordinal) ? "cur" : "new";
            sync.Upsert(conn, tx, Path.Combine(_root, folder, leaf, file), messageId, DateTimeOffset.UtcNow, "h", folder, null, null);
        }
        tx.Commit();
        return id;
    }

    private string[] RequestFiles() => Directory.GetFiles(_queue, "*" + MarkReadQueue.RequestExtension);

    [Fact]
    public void A_mixed_batch_reports_each_message_and_queues_only_the_eligible_ones()
    {
        Applier(DateTimeOffset.UtcNow, 60, "Alerts");
        var unseen = Seed("unseen@x", ("Alerts", "1.host,U=1:2,"));
        var seen = Seed("seen@x", ("Alerts", "2.host,U=2:2,S"));
        var inbox = Seed("inbox@x", ("INBOX", "3.host,U=3:2,"));

        var response = Build().MarkRead(ids: [unseen, seen, inbox, 9999]);

        response.Results.Select(r => r.Status).ShouldBe(
            [MarkReadStatus.Queued, MarkReadStatus.Unchanged, MarkReadStatus.ForbiddenFolder, MarkReadStatus.NotFound]);
        response.Results[0].Previous.ShouldBe(false);
        response.Results[1].Previous.ShouldBe(true);
        response.QueuedCount.ShouldBe(1);

        var file = RequestFiles().ShouldHaveSingleItem();
        File.ReadAllLines(file).ShouldBe([MarkReadQueue.RequestHeader, "read\tAlerts\t1.host,U=1"]);
    }

    [Fact]
    public void A_message_with_any_copy_outside_the_allowlist_is_forbidden_whole()
    {
        // Seen state is per email on the providers this targets: marking the
        // Alerts copy would mark the INBOX copy read on the next pull.
        Applier(DateTimeOffset.UtcNow, 60, "Alerts");
        var both = Seed("both@x", ("Alerts", "1.host,U=1:2,"), ("INBOX", "1.host,U=7:2,"));

        Build().MarkRead(ids: [both]).Results.Single().Status.ShouldBe(MarkReadStatus.ForbiddenFolder);
        RequestFiles().ShouldBeEmpty();
    }

    [Fact]
    public void The_star_allowlist_entry_allows_every_folder_and_every_unseen_copy_is_queued()
    {
        Applier(DateTimeOffset.UtcNow, 60, MarkReadQueue.AllFolders);
        var both = Seed("both@x", ("Alerts", "1.host,U=1:2,"), ("INBOX", "1.host,U=7:2,S"), ("INBOX", "new1.host,U=8"));

        Build().MarkRead(ids: [both]).Results.Single().Status.ShouldBe(MarkReadStatus.Queued);
        File.ReadAllLines(RequestFiles().ShouldHaveSingleItem()).Skip(1).ShouldBe(
            ["read\tAlerts\t1.host,U=1", "read\tINBOX\tnew1.host,U=8"], ignoreOrder: true);
    }

    [Theory]
    [InlineData(false, 60, true)]  // fresh
    [InlineData(true, 60, false)]  // missing
    [InlineData(false, 1, false)]  // stale: 1 s cadence, beat a minute old
    public void An_applier_that_is_not_reporting_gets_nothing_written(bool missing, int cadence, bool healthy)
    {
        if (!missing) Applier(DateTimeOffset.UtcNow.AddSeconds(-60), cadence, "Alerts");
        var id = Seed("m@x", ("Alerts", "1.host,U=1:2,"));

        var result = Build().MarkRead(ids: [id]).Results.Single();

        result.Status.ShouldBe(healthy ? MarkReadStatus.Queued : MarkReadStatus.Unavailable);
        RequestFiles().Length.ShouldBe(healthy ? 1 : 0);
        if (!healthy) result.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_applier_marker_with_no_folders_is_unavailable_not_allow_all()
    {
        Applier(DateTimeOffset.UtcNow);
        var id = Seed("m@x", ("Alerts", "1.host,U=1:2,"));

        Build().MarkRead(ids: [id]).Results.Single().Status.ShouldBe(MarkReadStatus.Unavailable);
    }

    [Fact]
    public void Unread_queues_only_seen_copies_and_a_repeat_is_unchanged()
    {
        Applier(DateTimeOffset.UtcNow, 60, "Alerts");
        var seen = Seed("seen@x", ("Alerts", "2.host,U=2:2,FS"));
        var unseen = Seed("unseen@x", ("Alerts", "1.host,U=1:2,"));

        var response = Build().MarkRead(messageIds: ["<seen@x>", "unseen@x"], read: false);

        response.Results.Select(r => r.Status).ShouldBe([MarkReadStatus.Queued, MarkReadStatus.Unchanged]);
        File.ReadAllLines(RequestFiles().ShouldHaveSingleItem()).Skip(1).ShouldBe(["unread\tAlerts\t2.host,U=2"]);
        _ = unseen;
    }

    [Fact]
    public void Duplicates_collapse_and_the_cap_applies_to_distinct_messages()
    {
        Applier(DateTimeOffset.UtcNow, 60, "Alerts");
        var a = Seed("a@x", ("Alerts", "1.host,U=1:2,"));
        var b = Seed("b@x", ("Alerts", "2.host,U=2:2,"));

        Build(maxPerCall: 2).MarkRead(ids: [a, a, b, a]).Results.Count.ShouldBe(2);
        Should.Throw<McpException>(() => Build(maxPerCall: 1).MarkRead(ids: [a, b])).Message.ShouldContain("At most 1");
    }

    [Fact]
    public void Exactly_one_of_ids_or_messageIds_is_required()
    {
        Should.Throw<McpException>(() => Build().MarkRead());
        Should.Throw<McpException>(() => Build().MarkRead(ids: [1], messageIds: ["x@y"]));
    }

    [Fact]
    public void A_tracked_path_outside_the_maildir_root_is_never_queued()
    {
        Applier(DateTimeOffset.UtcNow, 60, "Alerts");
        var id = new MessageRepository(_db.Connections).Upsert(
            Helpers.Sample("escape@x"), "Alerts", "Alerts/cur", "x", DateTimeOffset.UtcNow).Id;
        using (var conn = _db.Connections.Open())
        using (var tx = conn.BeginTransaction())
        {
            new SyncStateRepository(_db.Connections).Upsert(conn, tx, "/etc/Alerts/cur/x:2,", "escape@x",
                DateTimeOffset.UtcNow, "h", "Alerts", null, null);
            tx.Commit();
        }

        Build().MarkRead(ids: [id]).Results.Single().Status.ShouldBe(MarkReadStatus.NotFound);
        RequestFiles().ShouldBeEmpty();
    }

    [Fact]
    public void Request_files_are_named_in_apply_order_and_leave_no_temp_files()
    {
        Applier(DateTimeOffset.UtcNow, 60, "Alerts");
        var tool = Build();
        // Back to back, well inside one millisecond: name order must still be
        // call order, or the sidecar could apply a later request first.
        var ids = Enumerable.Range(0, 20).Select(i => Seed($"m{i}@x", ("Alerts", $"{i}.host,U={i}:2,"))).ToList();
        foreach (var id in ids) tool.MarkRead(ids: [id]);

        var names = Directory.GetFiles(_queue).Select(Path.GetFileName)
            .Where(n => n != MarkReadQueue.ApplierFileName).Order(StringComparer.Ordinal).ToList();
        names.Count.ShouldBe(20);
        names.ShouldAllBe(n => n!.EndsWith(MarkReadQueue.RequestExtension));
        names.Select(n => File.ReadAllLines(Path.Combine(_queue, n!))[1])
            .ShouldBe(Enumerable.Range(0, 20).Select(i => $"read\tAlerts\t{i}.host,U={i}"));
    }
}
