using Mailvec.Cli.Commands;
using Mailvec.Core.Eval;

namespace Mailvec.Cli.Tests;

/// <summary>
/// Coverage for the pure-logic helpers inside EvalAddCommand. The interactive
/// flow itself (search → pick → save) lives in EvalAddFlow and needs a DB +
/// console, so we target the argument parsers: mode aliases, filter assembly,
/// and the date validation that turns a typo'd --date-from into a loud error
/// instead of a silently-empty filter.
/// </summary>
public class EvalAddCommandHelpersTests
{
    [Theory]
    [InlineData("keyword", EvalMode.Keyword)]
    [InlineData("k", EvalMode.Keyword)]
    [InlineData("fts", EvalMode.Keyword)]
    [InlineData("semantic", EvalMode.Semantic)]
    [InlineData("vector", EvalMode.Semantic)]
    [InlineData("v", EvalMode.Semantic)]
    [InlineData("hybrid", EvalMode.Hybrid)]
    [InlineData("h", EvalMode.Hybrid)]
    [InlineData("HYBRID", EvalMode.Hybrid)]   // case-insensitive
    public void ParseMode_recognises_known_aliases(string input, EvalMode expected)
    {
        EvalAddCommand.ParseMode(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("")]
    [InlineData(" hybrid ")]   // whitespace not trimmed
    public void ParseMode_returns_null_for_unknown_input(string input)
    {
        EvalAddCommand.ParseMode(input).ShouldBeNull();
    }

    [Fact]
    public void BuildFilters_returns_null_when_no_filter_args_are_set()
    {
        EvalAddCommand.BuildFilters(null, null, null, null, null).ShouldBeNull();
    }

    [Fact]
    public void BuildFilters_returns_non_null_when_only_one_filter_is_set()
    {
        // A single non-null arg is enough to produce a filter object — the
        // null-return is reserved for the all-null case.
        var f = EvalAddCommand.BuildFilters("INBOX", null, null, null, null);
        f.ShouldNotBeNull();
        f!.Folder.ShouldBe("INBOX");
        f.FromContains.ShouldBeNull();
        f.DateFrom.ShouldBeNull();
    }

    [Fact]
    public void BuildFilters_passes_through_string_filters_verbatim()
    {
        var f = EvalAddCommand.BuildFilters("Archive.2024", "alice", "alice@example.com", null, null);
        f.ShouldNotBeNull();
        f!.Folder.ShouldBe("Archive.2024");
        f.FromContains.ShouldBe("alice");
        f.FromExact.ShouldBe("alice@example.com");
    }

    [Fact]
    public void BuildFilters_parses_plain_yyyy_MM_dd_dates_as_utc()
    {
        var f = EvalAddCommand.BuildFilters(null, null, null, "2024-01-15", "2024-12-31");
        f.ShouldNotBeNull();
        f!.DateFrom.ShouldBe(new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero));
        f.DateTo.ShouldBe(new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void BuildFilters_normalises_offset_dates_to_utc()
    {
        // AdjustToUniversal: a +05:00 timestamp must come back as the
        // equivalent instant in UTC so date comparisons stay offset-agnostic.
        var f = EvalAddCommand.BuildFilters(null, null, null, "2024-03-10T08:00:00+05:00", null);
        f.ShouldNotBeNull();
        f!.DateFrom.ShouldBe(new DateTimeOffset(2024, 3, 10, 3, 0, 0, TimeSpan.Zero));
        f.DateFrom!.Value.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("not-a-date", "--date-from")]
    [InlineData("2024-13-45", "--date-from")]
    public void BuildFilters_throws_with_flag_name_on_unparseable_date_from(string bad, string flag)
    {
        // The "typos error out" contract: an unparseable date must fail loudly
        // (naming the offending flag) rather than yield a null bound that
        // silently widens the query.
        var ex = Should.Throw<ArgumentException>(
            () => EvalAddCommand.BuildFilters(null, null, null, bad, null));
        ex.Message.ShouldContain(flag);
        ex.Message.ShouldContain(bad);
    }

    [Fact]
    public void BuildFilters_throws_with_flag_name_on_unparseable_date_to()
    {
        var ex = Should.Throw<ArgumentException>(
            () => EvalAddCommand.BuildFilters(null, null, null, null, "garbage"));
        ex.Message.ShouldContain("--date-to");
    }
}
