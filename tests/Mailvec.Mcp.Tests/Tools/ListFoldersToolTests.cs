using Mailvec.Core.Data;
using Mailvec.Mcp.Tools;

namespace Mailvec.Mcp.Tests.Tools;

public class ListFoldersToolTests
{
    private static ListFoldersTool Build(TempDatabase db) =>
        new(new MessageRepository(db.Connections), Helpers.Archive(), Helpers.NoopLogger());

    [Fact]
    public void Empty_archive_returns_zero_folders_with_setup_hint()
    {
        using var db = new TempDatabase();
        var resp = Build(db).ListFolders();

        resp.Count.ShouldBe(0);
        resp.Folders.ShouldBeEmpty();
        // The hint tells the client LLM WHY it's empty — which variant depends
        // on whether this machine has a shared config file, so just assert
        // presence here; SetupHintsTests pins the exact wording per branch.
        resp.SetupHint.ShouldNotBeNull();
        resp.SetupHint!.ShouldContain("empty");
    }

    [Fact]
    public void Returns_one_row_per_folder_alphabetically_with_counts()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        repo.Upsert(Helpers.Sample("a@x"), "INBOX",         "INBOX/cur",         "a", now);
        repo.Upsert(Helpers.Sample("b@x"), "INBOX",         "INBOX/cur",         "b", now);
        repo.Upsert(Helpers.Sample("c@x"), "Archive.2024",  "Archive.2024/cur",  "c", now);

        var resp = Build(db).ListFolders();

        resp.Count.ShouldBe(2);
        resp.Folders.Select(f => f.Folder).ShouldBe(new[] { "Archive.2024", "INBOX" });
        resp.Folders.First(f => f.Folder == "INBOX").MessageCount.ShouldBe(2);
        resp.Folders.First(f => f.Folder == "Archive.2024").MessageCount.ShouldBe(1);
    }

    [Fact]
    public void Soft_deleted_messages_are_excluded_from_counts()
    {
        using var db = new TempDatabase();
        var repo = new MessageRepository(db.Connections);
        var now = DateTimeOffset.UtcNow;

        repo.Upsert(Helpers.Sample("a@x"), "INBOX", "INBOX/cur", "a", now);
        long b = repo.Upsert(Helpers.Sample("b@x"), "INBOX", "INBOX/cur", "b", now);
        repo.MarkDeleted([b], DateTimeOffset.UtcNow);

        var resp = Build(db).ListFolders();

        resp.Folders.Single().MessageCount.ShouldBe(1);
    }
}
