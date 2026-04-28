using Mailvec.Core.Eval;

namespace Mailvec.Core.Tests.Eval;

public sealed class EvalQuerySetTests
{
    [Fact]
    public void RoundTrip_BareStringRelevant_PreservesGradeOne()
    {
        var json = """
            {
              "version": 1,
              "queries": [
                { "id": "q001", "query": "lease", "relevant": ["<a@x>", "<b@x>"] }
              ]
            }
            """;
        var path = WriteTemp(json);
        try
        {
            var set = EvalQuerySet.Load(path);
            set.Queries.Count.ShouldBe(1);
            set.Queries[0].Relevant.Select(r => r.Grade).ShouldAllBe(g => g == 1.0);
            set.Queries[0].Relevant.Select(r => r.MessageId).ShouldBe(new[] { "<a@x>", "<b@x>" });
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RoundTrip_GradedRelevance_PreservesGrade()
    {
        var json = """
            {
              "version": 1,
              "queries": [
                {
                  "id": "q002",
                  "query": "anthropic invoice",
                  "relevant": [
                    { "messageId": "<hi@x>", "grade": 3 },
                    "<lo@x>"
                  ]
                }
              ]
            }
            """;
        var path = WriteTemp(json);
        try
        {
            var set = EvalQuerySet.Load(path);
            var rel = set.Queries[0].Relevant;
            rel.Count.ShouldBe(2);
            rel[0].MessageId.ShouldBe("<hi@x>");
            rel[0].Grade.ShouldBe(3.0);
            rel[1].MessageId.ShouldBe("<lo@x>");
            rel[1].Grade.ShouldBe(1.0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_EmitsBareStringForGradeOne_AndObjectForGraded()
    {
        var set = new EvalQuerySet
        {
            Queries =
            {
                new EvalQuery
                {
                    Id = "q001",
                    Query = "test",
                    Relevant = { new RelevantEntry("<a@x>"), new RelevantEntry("<b@x>", 2.5) },
                },
            },
        };
        var path = Path.Combine(Path.GetTempPath(), $"mailvec-eval-{Guid.NewGuid():N}.json");
        try
        {
            set.Save(path);
            var text = File.ReadAllText(path);
            text.ShouldContain("\"<a@x>\"");
            text.ShouldContain("\"messageId\": \"<b@x>\"");
            text.ShouldContain("\"grade\": 2.5");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_DuplicateIds_Throws()
    {
        var json = """
            {
              "version": 1,
              "queries": [
                { "id": "q001", "query": "a", "relevant": ["<a@x>"] },
                { "id": "q001", "query": "b", "relevant": ["<b@x>"] }
              ]
            }
            """;
        var path = WriteTemp(json);
        try
        {
            Should.Throw<InvalidDataException>(() => EvalQuerySet.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NextSequentialId_FindsHighestQNumber()
    {
        var set = new EvalQuerySet
        {
            Queries =
            {
                new EvalQuery { Id = "q001", Query = "a" },
                new EvalQuery { Id = "q005", Query = "b" },
                new EvalQuery { Id = "custom-name", Query = "c" },
            },
        };
        set.NextSequentialId().ShouldBe("q006");
    }

    [Fact]
    public void NextSequentialId_EmptySet_IsQ001()
    {
        new EvalQuerySet().NextSequentialId().ShouldBe("q001");
    }

    [Fact]
    public void EvalQueryFilters_ToSearchFilters_PreservesAllFields()
    {
        var f = new EvalQueryFilters
        {
            Folder = "INBOX",
            DateFrom = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            FromExact = "x@y.com",
        };
        var sf = f.ToSearchFilters();
        sf.Folder.ShouldBe("INBOX");
        sf.DateFrom.ShouldBe(f.DateFrom);
        sf.FromExact.ShouldBe("x@y.com");
        sf.IsEmpty.ShouldBeFalse();
    }

    private static string WriteTemp(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mailvec-eval-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents);
        return path;
    }
}
