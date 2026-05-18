using Mailvec.Cli.Commands;

namespace Mailvec.Cli.Tests;

/// <summary>
/// Coverage for the pure-logic helpers inside DoctorCommand. The full Run is
/// a 200-line async fan-out (launchctl shell-outs, optional Ollama ping,
/// HTTP /health probe) — we leave the orchestration alone and test only
/// the formatters + summary tallier here.
/// </summary>
public class DoctorCommandHelpersTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(2 * 1024 * 1024, "2.0 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.00 GB")]
    public void FormatSize_renders_powers_of_two(long bytes, string expected)
    {
        DoctorCommand.FormatSize(bytes).ShouldBe(expected);
    }

    [Theory]
    [InlineData(30, "30s")]
    [InlineData(120, "2m")]
    [InlineData(60 * 60 * 5, "5h")]
    [InlineData(60 * 60 * 24 * 7, "7d")]
    public void HumanizeAge_picks_appropriate_unit(int seconds, string expected)
    {
        DoctorCommand.HumanizeAge(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);
    }

    [Fact]
    public void Summarize_tallies_status_counts()
    {
        var checks = new List<DoctorCommand.DoctorCheck>
        {
            new("a", "ok",   "all good",     "config"),
            new("b", "warn", "minor issue",  "config"),
            new("c", "fail", "broken",       "services"),
            new("d", "ok",   "all good",     "tools"),
            new("e", "warn", "minor issue",  "tools"),
            // An unknown status should be ignored (defensive: future
            // refactors can't accidentally inflate any of the three buckets).
            new("f", "unknown", "?",         "tools"),
        };

        var (ok, warn, fail) = DoctorCommand.Summarize(checks);

        ok.ShouldBe(2);
        warn.ShouldBe(2);
        fail.ShouldBe(1);
    }

    [Fact]
    public void Summarize_returns_zeros_for_empty_list()
    {
        var (ok, warn, fail) = DoctorCommand.Summarize(new List<DoctorCommand.DoctorCheck>());
        ok.ShouldBe(0);
        warn.ShouldBe(0);
        fail.ShouldBe(0);
    }
}
