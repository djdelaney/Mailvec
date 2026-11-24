using Mailvec.Core.Search;

namespace Mailvec.Core.Tests.Search;

public class SearchDateParserTests
{
    [Fact]
    public void Date_only_upper_bound_expands_to_end_of_day()
    {
        // Every surface documents dateTo as inclusive and compares with <=;
        // parsing "2024-12-31" to midnight silently drops the whole boundary
        // day — the common shape an LLM client passes.
        SearchDateParser.TryParse("2024-12-31", isUpperBound: true, out var bound).ShouldBeTrue();

        var b = bound.ShouldNotBeNull();
        b.Year.ShouldBe(2024);
        b.Month.ShouldBe(12);
        b.Day.ShouldBe(31);
        b.Hour.ShouldBe(23);
        b.Minute.ShouldBe(59);
        b.Second.ShouldBe(59);
    }

    [Fact]
    public void Date_only_lower_bound_stays_at_midnight()
    {
        SearchDateParser.TryParse("2024-01-01", isUpperBound: false, out var bound).ShouldBeTrue();
        var b = bound.ShouldNotBeNull();
        b.Hour.ShouldBe(0);
        b.Minute.ShouldBe(0);
        b.Second.ShouldBe(0);
    }

    [Fact]
    public void Explicit_time_component_is_respected_verbatim_for_upper_bound()
    {
        SearchDateParser.TryParse("2024-12-31T12:00:00Z", isUpperBound: true, out var bound).ShouldBeTrue();
        var b = bound.ShouldNotBeNull();
        b.Hour.ShouldBe(12);
        b.Day.ShouldBe(31);
    }

    [Fact]
    public void Empty_and_whitespace_parse_to_no_bound()
    {
        SearchDateParser.TryParse(null, isUpperBound: true, out var a).ShouldBeTrue();
        a.ShouldBeNull();
        SearchDateParser.TryParse("  ", isUpperBound: false, out var b).ShouldBeTrue();
        b.ShouldBeNull();
    }

    [Fact]
    public void Garbage_reports_failure()
    {
        SearchDateParser.TryParse("not-a-date", isUpperBound: true, out _).ShouldBeFalse();
    }

    [Fact]
    public void Trimmed_date_only_still_expands()
    {
        SearchDateParser.TryParse(" 2024-06-30 ", isUpperBound: true, out var bound).ShouldBeTrue();
        bound.ShouldNotBeNull().Hour.ShouldBe(23);
    }

    [Fact]
    public void The_last_representable_day_is_a_valid_upper_bound()
    {
        // "Everything up to the end of time" is a shape a client really sends.
        // Stepping to the next midnight first overflowed DateTimeOffset here.
        SearchDateParser.TryParse("9999-12-31", isUpperBound: true, out var bound).ShouldBeTrue();

        var b = bound.ShouldNotBeNull();
        (b.Year, b.Month, b.Day, b.Hour, b.Minute, b.Second).ShouldBe((9999, 12, 31, 23, 59, 59));
    }
}
