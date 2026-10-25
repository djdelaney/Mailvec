using Mailvec.Core.Data;
using Mailvec.Core.Search;

namespace Mailvec.DevCorpus.Tests;

/// <summary>
/// What the Gmail layout adds beyond the per-scenario folder sets
/// (<see cref="GmailScenarioTests"/>): the corpus really is several files per
/// message, the bracketed and spaced system folder names work as filters and
/// in list_folders, read state agrees across copies, and an unchanged rescan
/// over N copies does nothing.
/// </summary>
public class GmailLayoutTests(GmailCorpus fixture) : IClassFixture<GmailCorpus>
{
    private long Count(string sql, params (string, object?)[] args) =>
        fixture.Query(sql, r => { r.Read(); return r.GetInt64(0); }, args);

    [Fact]
    public void Every_message_is_one_row_however_many_labels_it_has()
    {
        var files = fixture.Corpus.AllMailFiles.Count();
        var tracked = Count("SELECT COUNT(*) FROM sync_state");
        var live = Count("SELECT COUNT(*) FROM messages WHERE deleted_at IS NULL");

        tracked.ShouldBe(files);
        // Not a vacuous layout: nearly everything has an All Mail copy.
        tracked.ShouldBeGreaterThan(live * 3 / 2);
        Count("SELECT COUNT(*) FROM (SELECT message_id FROM messages GROUP BY message_id HAVING COUNT(*) > 1)").ShouldBe(0);
    }

    [Fact]
    public void All_mail_holds_everything_but_trash()
    {
        var notInAllMail = fixture.Query(
            """
            SELECT m.message_id FROM messages m
            WHERE m.deleted_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM sync_state s WHERE s.message_id = m.message_id AND s.folder = $f)
            """,
            r => { var l = new List<string>(); while (r.Read()) l.Add(r.GetString(0)); return l; },
            ("$f", Layouts.AllMail));
        notInAllMail.ShouldBe(["s29-coupon@shop.example"]);
    }

    [Fact]
    public void System_folder_names_work_as_search_filters()
    {
        var keyword = new KeywordSearchService(fixture.Connections);
        keyword.Search("zellige", 10, new SearchFilters(Folder: Layouts.AllMail)).Count.ShouldBe(1);
        keyword.Search("zellige", 10, new SearchFilters(Folder: Layouts.Important)).Count.ShouldBe(1);
        keyword.Search("SEEDS20", 10, new SearchFilters(Folder: "[Gmail]/Trash")).Count.ShouldBe(1);
        keyword.Search("SEEDS20", 10, new SearchFilters(Folder: Layouts.AllMail)).Count.ShouldBe(0);
        // A filter on the label's parent is not a filter on its children.
        keyword.Search("zellige", 10, new SearchFilters(Folder: "[Gmail]")).Count.ShouldBe(0);
    }

    [Fact]
    public void List_folders_counts_each_label_and_never_the_bracketed_parent()
    {
        var stats = new MessageRepository(fixture.Connections).FolderStats().ToDictionary(f => f.Folder, f => f.MessageCount);
        var live = Count("SELECT COUNT(*) FROM messages WHERE deleted_at IS NULL");

        stats.ShouldNotContainKey("[Gmail]");
        stats[Layouts.AllMail].ShouldBe(live - 1); // the trashed coupon
        stats[Layouts.Starred].ShouldBe(1);
        stats.ShouldContainKey("[Gmail]/Sent Mail");
        stats.ShouldContainKey("Archive/2025");
    }

    [Fact]
    public void Read_state_agrees_across_every_copy()
    {
        var states = new MessageRepository(fixture.Connections).GetReadStates(
            ["s02-locker@parcelpoint.example", "s01-timber@example.com", "s13-quote@tiler.example"]);
        states["s02-locker@parcelpoint.example"].ShouldBeFalse(); // unread in INBOX, All Mail and Important
        states["s01-timber@example.com"].ShouldBeTrue();          // "FS": starred and seen
        states["s13-quote@tiler.example"].ShouldBeTrue();
    }

    [Fact]
    public void Each_message_is_attributed_to_a_folder_it_is_actually_in()
    {
        Count(
            """
            SELECT COUNT(*) FROM messages m
            WHERE m.deleted_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM sync_state s WHERE s.message_id = m.message_id AND s.folder = m.folder)
            """).ShouldBe(0);
    }

    [Fact]
    public void An_unchanged_rescan_over_every_copy_does_nothing()
    {
        var again = fixture.Scanner.ScanAll();
        again.Upserted.ShouldBe(0);
        again.SoftDeleted.ShouldBe(0);
        again.Unchanged.ShouldBe(fixture.Corpus.AllMailFiles.Count());
    }
}
