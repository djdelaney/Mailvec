using System.Globalization;

namespace Mailvec.Core.Search;

/// <summary>
/// Shared parsing for the dateFrom/dateTo filter bounds so the three surfaces
/// (MCP tool, tray, CLI) keep identical semantics — the same reason
/// SearchFilterSql is shared.
///
/// The load-bearing rule: a date-only UPPER bound ("2024-12-31") means the
/// END of that day. Every surface documents dateTo as inclusive, and the SQL
/// comparison is a datetime()-normalized <c>&lt;=</c> — parsing the value to
/// midnight silently drops every message sent later that day, losing up to a
/// full day of recall on the most common client-supplied shape. Date-only
/// lower bounds keep midnight (correct inclusive start). Values that carry an
/// explicit time component are used as given for both bounds.
/// </summary>
public static class SearchDateParser
{
    /// <summary>
    /// Parses an ISO-8601 date or datetime bound. Returns false when the
    /// value is non-empty but unparseable (callers decide whether that's an
    /// error or an ignored filter). An empty value parses to null (no bound).
    /// </summary>
    public static bool TryParse(string? value, bool isUpperBound, out DateTimeOffset? bound)
    {
        bound = null;
        if (string.IsNullOrWhiteSpace(value)) return true;

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return false;

        if (isUpperBound && IsDateOnly(value))
        {
            // End of the named day. AddTicks(-1) rather than "next midnight
            // exclusive" because SearchFilterSql compares with <=; the
            // datetime() wrapper truncates to whole seconds, so 23:59:59.9999999
            // compares as 23:59:59 and still includes everything on the day.
            parsed = parsed.AddDays(1).AddTicks(-1);
        }

        bound = parsed;
        return true;
    }

    private static bool IsDateOnly(string value)
    {
        var v = value.Trim();
        // yyyy-MM-dd exactly — anything longer carries a time (or offset) and
        // is respected verbatim.
        return v.Length == 10
            && v[4] == '-' && v[7] == '-'
            && char.IsAsciiDigit(v[0]) && char.IsAsciiDigit(v[1]) && char.IsAsciiDigit(v[2]) && char.IsAsciiDigit(v[3])
            && char.IsAsciiDigit(v[5]) && char.IsAsciiDigit(v[6])
            && char.IsAsciiDigit(v[8]) && char.IsAsciiDigit(v[9]);
    }
}
