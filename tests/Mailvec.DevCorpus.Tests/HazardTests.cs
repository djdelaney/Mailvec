namespace Mailvec.DevCorpus.Tests;

/// <summary>The --hazards corpus, no filler: scenarios plus the four hazard cases.</summary>
public sealed class HazardCorpus() : IndexedCorpus(new CorpusOptions(Hazards: true, Filler: 0));

/// <summary>What the real indexer does with each hazard.</summary>
public class HazardTests(HazardCorpus fixture) : IClassFixture<HazardCorpus>
{
    private string? Status(string messageId, string fileName) =>
        fixture.Query(
            "SELECT a.extraction_status FROM attachments a JOIN messages m ON m.id = a.message_id WHERE m.message_id = $m AND a.filename = $f",
            r => r.Read() ? (r.IsDBNull(0) ? null : r.GetString(0)) : "<no row>",
            ("$m", messageId), ("$f", fileName));

    [Fact]
    public void Hazards_never_fail_the_scan()
    {
        fixture.Scan.FailedToParse.ShouldBe(0);
        fixture.Scan.Incomplete.ShouldBeFalse();
    }

    [Fact]
    public void The_zip_bomb_fails_its_attachment_and_nothing_else() =>
        Status("h01-bomb@hazard.example", "bomb.docx").ShouldBe("failed");

    [Fact]
    public void The_oversize_attachment_is_skipped_undecoded() =>
        Status("h02-oversize@hazard.example", "huge-scan.pdf").ShouldBe("oversize");

    [Fact]
    public void A_symlink_out_of_the_maildir_is_not_followed()
    {
        fixture.Scan.SymlinksSkipped.ShouldBe(1);
        fixture.Query("SELECT COUNT(*) FROM messages WHERE message_id = 'h03-outside@hazard.example'",
            r => { r.Read(); return r.GetInt64(0); }).ShouldBe(0);
    }

    [Fact]
    public void A_folder_named_tmp_is_not_indexed() =>
        fixture.Query("SELECT COUNT(*) FROM messages WHERE message_id = 'h04-tmp@hazard.example'",
            r => { r.Read(); return r.GetInt64(0); }).ShouldBe(0);
}
