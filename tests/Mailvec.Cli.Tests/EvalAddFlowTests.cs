using Mailvec.Cli.Commands;
using Mailvec.Core.Eval;

namespace Mailvec.Cli.Tests;

/// <summary>
/// The eval-add save flow's non-interactive behavior. Under `dotnet test`
/// (and any script), stdin is redirected — which is exactly the environment
/// where the old "Save? [y/N]" prompt read null, printed "(not saved)", and
/// exited 0: a silent no-op that looked like success to the calling script.
/// </summary>
public sealed class EvalAddFlowTests : IDisposable
{
    private readonly TestServiceProvider _sp = new();

    public void Dispose() => _sp.Dispose();

    private void InsertMessage(string messageId)
    {
        using var conn = _sp.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (message_id, maildir_path, maildir_filename, folder, subject, size_bytes, indexed_at)
            VALUES ($mid, 'INBOX/cur', 'f', 'INBOX', 's', 100, $now)
            """;
        cmd.Parameters.AddWithValue("$mid", messageId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private EvalAddFlow.Args Args(bool yes, string querySetPath) => new(
        Query: "test query",
        Filters: null,
        Mode: EvalMode.Keyword,
        TopK: 10,
        IdOverride: null,
        Notes: null,
        PinRelevantIds: ["pinned@x"],
        Yes: yes,
        QuerySetPath: querySetPath);

    [Fact]
    public async Task Scripted_save_without_yes_fails_loudly_instead_of_silently_saving_nothing()
    {
        // Guard depends on redirected stdin, which is how the test runner
        // (and any script) invokes the CLI. On a real TTY the prompt shows.
        if (!Console.IsInputRedirected) return;

        InsertMessage("pinned@x");
        var querySet = Path.Combine(_sp.DirectoryPath, "queries.json");

        var exit = await EvalAddFlow.RunAsync(_sp.Services, Args(yes: false, querySet), default);

        exit.ShouldBe(2);
        File.Exists(querySet).ShouldBeFalse(); // nothing silently written either
    }

    [Fact]
    public async Task Scripted_save_with_yes_saves_and_exits_zero()
    {
        InsertMessage("pinned@x");
        var querySet = Path.Combine(_sp.DirectoryPath, "queries.json");

        var exit = await EvalAddFlow.RunAsync(_sp.Services, Args(yes: true, querySet), default);

        exit.ShouldBe(0);
        var saved = EvalQuerySet.LoadOrEmpty(querySet);
        saved.Queries.Count.ShouldBe(1);
        saved.Queries[0].Relevant.Single().MessageId.ShouldBe("pinned@x");
    }
}
