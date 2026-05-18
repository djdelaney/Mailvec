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
}
