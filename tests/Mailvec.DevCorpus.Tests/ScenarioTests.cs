namespace Mailvec.DevCorpus.Tests;

/// <summary>
/// Every scenario's expectation, checked against what the real indexer made
/// of it. A failure here means either the pipeline changed behaviour for
/// that shape of mail, or the scenario no longer builds what its description
/// says — both worth knowing before anyone relies on the corpus.
/// </summary>
public class ScenarioTests(IndexedCorpus fixture) : IClassFixture<IndexedCorpus>
{
    public static TheoryData<string> ScenarioIds()
    {
        var data = new TheoryData<string>();
        foreach (var s in CorpusWriter.Build(new CorpusOptions()).Scenarios) data.Add(s.Id);
        return data;
    }

    private sealed record Row(long Id, string MessageId, string? ThreadId, string? Body, string? DateSent);

    private Row Find(Scenario s)
    {
        var (sql, arg) = s.MessageId is { } mid
            ? ("message_id = $k", (object)mid)
            : ("subject = $k", s.Subject);
        var rows = fixture.Query(
            $"SELECT id, message_id, thread_id, body_text, date_sent FROM messages WHERE deleted_at IS NULL AND {sql}",
            r =>
            {
                var list = new List<Row>();
                while (r.Read())
                    list.Add(new Row(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                        r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
                return list;
            },
            ("$k", arg));
        rows.Count.ShouldBe(1, $"{s.Id}: expected exactly one live messages row");
        return rows[0];
    }

    [Fact]
    public void The_whole_corpus_indexes_with_no_parse_failures()
    {
        fixture.Scan.FailedToParse.ShouldBe(0);
        fixture.Scan.SoftDeleted.ShouldBe(0);

        var distinctMessages = fixture.Corpus.Scenarios.Select(s => s.MessageId ?? s.Subject).Distinct().Count();
        var live = fixture.Query("SELECT COUNT(*) FROM messages WHERE deleted_at IS NULL", r => { r.Read(); return r.GetInt64(0); });
        live.ShouldBe(distinctMessages + IndexedCorpus.Filler);
    }

    [Theory]
    [MemberData(nameof(ScenarioIds))]
    public void Scenario_is_indexed_as_described(string id)
    {
        var s = fixture.Corpus.Scenarios.Single(x => x.Id == id);
        var e = s.Expect;
        var row = Find(s);

        if (e.Folders is { } folders)
        {
            var actual = fixture.Query(
                "SELECT DISTINCT folder FROM sync_state WHERE message_id = $m ORDER BY folder",
                r => { var l = new List<string>(); while (r.Read()) l.Add(r.GetString(0)); return l; },
                ("$m", row.MessageId));
            actual.ShouldBe(folders.Order(StringComparer.Ordinal).ToList(), $"{id}: folder membership");
        }

        foreach (var text in e.BodyContains ?? [])
            (row.Body ?? "").ShouldContain(text, Case.Sensitive, $"{id}: body should contain '{text}'");
        foreach (var text in e.BodyExcludes ?? [])
            (row.Body ?? "").ShouldNotContain(text, Case.Insensitive, $"{id}: body should not contain '{text}'");

        if (e.ThreadId is { } thread) row.ThreadId.ShouldBe(thread, $"{id}: thread_id");

        if (e.DateSentNull) row.DateSent.ShouldBeNull($"{id}: date_sent");
        if (e.DateSentUtc is { } when)
            DateTimeOffset.Parse(row.DateSent.ShouldNotBeNull(), System.Globalization.CultureInfo.InvariantCulture)
                .ShouldBe(when, $"{id}: date_sent instant");

        foreach (var a in e.Attachments ?? [])
        {
            var att = fixture.Query(
                "SELECT part_index, extraction_status, extracted_text FROM attachments WHERE message_id = $id AND filename = $f",
                r =>
                {
                    var l = new List<(int Part, string? Status, string? Text)>();
                    while (r.Read())
                        l.Add((r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
                    return l;
                },
                ("$id", row.Id), ("$f", a.FileName));
            att.Count.ShouldBe(1, $"{id}: one attachment row named '{a.FileName}'");
            att[0].Status.ShouldBe(a.Status, $"{id}: extraction_status of '{a.FileName}'");
            if (a.TextContains is { } t)
                (att[0].Text ?? "").ShouldContain(t, Case.Insensitive, $"{id}: text of '{a.FileName}'");
            if (a.PartIndex is { } p) att[0].Part.ShouldBe(p, $"{id}: part_index of '{a.FileName}'");
        }
    }

    [Fact]
    public void Mixed_offset_dates_sort_by_instant_not_by_string()
    {
        // The pair exists for this: as ISO text the later message sorts first.
        var later = Find(fixture.Corpus.Scenarios.Single(s => s.Id == "s09-offset-later")).DateSent!;
        var earlier = Find(fixture.Corpus.Scenarios.Single(s => s.Id == "s09-offset-earlier")).DateSent!;
        string.CompareOrdinal(later, earlier).ShouldBeLessThan(0, "the pair no longer demonstrates the string-order trap");
        fixture.Query("SELECT datetime($a) > datetime($b)", r => { r.Read(); return r.GetInt64(0); }, ("$a", later), ("$b", earlier))
            .ShouldBe(1);
    }

    [Fact]
    public void Every_eval_target_is_eligible_for_a_vector()
    {
        // The embedder gives a message no chunks when its body is under
        // MinBodyCharsForVector and it has no attachment text. An eval target
        // like that can never be found by the semantic leg, so the eval would
        // measure the corpus rather than the search.
        var min = new Mailvec.Core.Options.EmbedderOptions().MinBodyCharsForVector;
        foreach (var mid in fixture.Corpus.Eval.SelectMany(q => q.Relevant).Distinct())
        {
            fixture.Query(
                "SELECT length(trim(coalesce(body_text, ''))) >= $min OR coalesce(attachment_text, '') <> '' FROM messages WHERE message_id = $m",
                r => { r.Read(); return r.GetInt64(0); }, ("$m", mid), ("$min", min))
                .ShouldBe(1, $"{mid} gets no vector at MinBodyCharsForVector={min}");
        }
    }

    [Fact]
    public void Most_filler_is_eligible_for_a_vector()
    {
        var min = new Mailvec.Core.Options.EmbedderOptions().MinBodyCharsForVector;
        fixture.Query(
            "SELECT COUNT(*) FROM messages WHERE message_id LIKE 'filler-%' AND length(trim(coalesce(body_text, ''))) < $min",
            r => { r.Read(); return r.GetInt64(0); }, ("$min", min)).ShouldBe(0);
    }

    [Fact]
    public void Every_eval_label_resolves_to_an_indexed_message()
    {
        foreach (var q in fixture.Corpus.Eval)
        foreach (var mid in q.Relevant)
        {
            fixture.Query("SELECT COUNT(*) FROM messages WHERE deleted_at IS NULL AND message_id = $m",
                r => { r.Read(); return r.GetInt64(0); }, ("$m", mid)).ShouldBe(1, $"{q.Id} labels {mid}");
        }
    }
}
