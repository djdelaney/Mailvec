using Mailvec.Cli.Commands;
using Mailvec.Core.Eval;

namespace Mailvec.Cli.Tests;

/// <summary>
/// Coverage for the pure-logic helpers inside EvalCommand. The async Run
/// itself spans 400+ lines of console formatting that don't make sense to
/// unit-test (and the eval pipeline behind it is exercised in
/// <c>Mailvec.Core.Tests/Eval/EvalRunnerTests</c>), so we target the
/// per-string parsers and formatters used by the CLI argument and output
/// surface.
/// </summary>
public class EvalCommandHelpersTests
{
    [Theory]
    [InlineData("all", 3)]
    [InlineData("keyword", 1)]
    [InlineData("k", 1)]
    [InlineData("fts", 1)]
    [InlineData("semantic", 1)]
    [InlineData("vector", 1)]
    [InlineData("v", 1)]
    [InlineData("hybrid", 1)]
    [InlineData("h", 1)]
    [InlineData("HYBRID", 1)]   // case-insensitive
    public void ParseModes_recognises_known_aliases(string input, int expectedCount)
    {
        var modes = EvalCommand.ParseModes(input);
        modes.ShouldNotBeNull();
        modes!.Count.ShouldBe(expectedCount);
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("")]
    [InlineData(" hybrid ")]      // whitespace not trimmed
    public void ParseModes_returns_null_for_unknown_input(string input)
    {
        EvalCommand.ParseModes(input).ShouldBeNull();
    }

    [Fact]
    public void ParseModes_all_returns_keyword_semantic_hybrid_in_that_order()
    {
        var modes = EvalCommand.ParseModes("all");
        modes.ShouldNotBeNull();
        modes!.ShouldBe([EvalMode.Keyword, EvalMode.Semantic, EvalMode.Hybrid]);
    }

    [Theory]
    [InlineData(EvalMode.Keyword, "keyword")]
    [InlineData(EvalMode.Semantic, "semantic")]
    [InlineData(EvalMode.Hybrid, "hybrid")]
    public void ModeName_returns_canonical_label(EvalMode mode, string expected)
    {
        EvalCommand.ModeName(mode).ShouldBe(expected);
    }

    [Theory]
    [InlineData(0.0, "  =0.000")]
    [InlineData(0.0001, "  =0.000")]  // below threshold → equal
    [InlineData(0.001, "+0.001")]
    [InlineData(-0.123, "-0.123")]
    [InlineData(1.234, "+1.234")]
    public void Delta_pads_zero_and_signs_non_zero(double input, string expected)
    {
        EvalCommand.Delta(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData(0.0, "  =0.0")]
    [InlineData(0.01, "  =0.0")]     // below threshold
    [InlineData(0.5, "+0.5")]
    [InlineData(-12.3, "-12.3")]
    [InlineData(123.4, "+123.4")]
    public void DeltaMs_pads_zero_and_signs_non_zero(double input, string expected)
    {
        EvalCommand.DeltaMs(input).ShouldBe(expected);
    }

    [Fact]
    public void FilterSummary_returns_empty_string_when_no_filters_set()
    {
        EvalCommand.FilterSummary(new EvalQueryFilters()).ShouldBe(string.Empty);
    }

    [Fact]
    public void FilterSummary_joins_all_set_filters_with_commas()
    {
        var f = new EvalQueryFilters
        {
            Folder = "INBOX",
            DateFrom = DateTimeOffset.Parse("2024-01-15T00:00:00Z"),
            DateTo = DateTimeOffset.Parse("2024-12-31T00:00:00Z"),
            FromContains = "alice",
            FromExact = "alice@example.com",
        };

        var summary = EvalCommand.FilterSummary(f);

        summary.ShouldContain("folder=INBOX");
        summary.ShouldContain("dateFrom=2024-01-15");
        summary.ShouldContain("dateTo=2024-12-31");
        summary.ShouldContain("fromContains=alice");
        summary.ShouldContain("fromExact=alice@example.com");
    }

    [Fact]
    public void FilterSummary_omits_unset_filters()
    {
        var f = new EvalQueryFilters { Folder = "INBOX" };
        var summary = EvalCommand.FilterSummary(f);

        summary.ShouldBe("folder=INBOX");
        summary.ShouldNotContain("dateFrom");
        summary.ShouldNotContain("fromContains");
    }
}
