using Mailvec.Cli.Commands;
using Mailvec.Core.Eval;

namespace Mailvec.Cli.Tests;

public class EvalImportCommandHelpersTests
{
    [Theory]
    [InlineData(15, "15s ago")]
    [InlineData(120, "2m ago")]
    [InlineData(60 * 60 * 3, "3h ago")]
    [InlineData(60 * 60 * 24 * 5, "5d ago")]
    public void HumanizeAge_picks_appropriate_unit(int seconds, string expected)
    {
        EvalImportCommand.HumanizeAge(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, EvalMode.Hybrid)]       // default
    [InlineData("hybrid", EvalMode.Hybrid)]
    [InlineData("keyword", EvalMode.Keyword)]
    [InlineData("fts", EvalMode.Keyword)]
    [InlineData("semantic", EvalMode.Semantic)]
    [InlineData("vector", EvalMode.Semantic)]
    [InlineData("VECTOR", EvalMode.Semantic)]
    [InlineData("garbage", EvalMode.Hybrid)]  // unknown falls back to hybrid
    public void ParseMode_handles_aliases_and_defaults(string? input, EvalMode expected)
    {
        EvalImportCommand.ParseMode(input).ShouldBe(expected);
    }

    [Fact]
    public void LoadStdioLog_parses_calls_and_dates_relative_to_mtime()
    {
        // Three lines on one day, two on the prior day (crossing midnight backwards).
        var lines = new[]
        {
            "[09:00:15 INF] mcp-call tool=search_emails args={\"query\":\"alpha\"}",
            "[23:55:00 INF] mcp-call tool=search_emails args={\"query\":\"beta\"}",
            "[00:10:00 INF] mcp-call tool=search_emails args={\"query\":\"gamma\"}",
            "[14:20:30 INF] mcp-call tool=search_emails args={\"query\":\"delta\",\"mode\":\"keyword\"}",
            "[16:00:00 INF] mcp-call tool=get_email args={\"id\":\"<ignored>\"}", // wrong tool, must skip
        };
        var mtime = new DateTime(2026, 5, 27, 14, 25, 0, DateTimeKind.Local);

        var sink = new List<EvalImportCommand.RecentCall>();
        EvalImportCommand.LoadStdioLog(lines, mtime, sink);

        sink.Select(c => c.Args.Query).ShouldBe(new[] { "delta", "gamma", "beta", "alpha" });
        sink.Single(c => c.Args.Query == "delta").Timestamp.Date.ShouldBe(new DateTime(2026, 5, 27));
        sink.Single(c => c.Args.Query == "gamma").Timestamp.Date.ShouldBe(new DateTime(2026, 5, 27));
        sink.Single(c => c.Args.Query == "beta").Timestamp.Date.ShouldBe(new DateTime(2026, 5, 26));
        sink.Single(c => c.Args.Query == "alpha").Timestamp.Date.ShouldBe(new DateTime(2026, 5, 26));
        sink.Single(c => c.Args.Query == "delta").Args.Mode.ShouldBe("keyword");
    }

    [Fact]
    public void LoadStdioLog_shifts_all_lines_back_when_last_line_is_after_mtime_time_of_day()
    {
        // mtime says "today at 08:00" but the trailing line is at 22:00 — file
        // was last touched yesterday evening and hasn't been written today.
        var lines = new[]
        {
            "[21:00:00 INF] mcp-call tool=search_emails args={\"query\":\"earlier\"}",
            "[22:00:00 INF] mcp-call tool=search_emails args={\"query\":\"later\"}",
        };
        var mtime = new DateTime(2026, 5, 27, 8, 0, 0, DateTimeKind.Local);

        var sink = new List<EvalImportCommand.RecentCall>();
        EvalImportCommand.LoadStdioLog(lines, mtime, sink);

        sink.Count.ShouldBe(2);
        sink.ShouldAllBe(c => c.Timestamp.Date == new DateTime(2026, 5, 26));
    }
}
