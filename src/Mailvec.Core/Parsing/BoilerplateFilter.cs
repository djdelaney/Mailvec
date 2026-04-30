using System.Text.RegularExpressions;

namespace Mailvec.Core.Parsing;

/// <summary>
/// Line-level filter for marketing-email boilerplate that DOM-level cleanup
/// can't catch reliably: copyright notices, "view in browser" headers,
/// "manage preferences" footers, reference IDs, etc. Patterns anchor on the
/// start of a (trimmed) line and are conservative — phrases that could
/// plausibly appear inside real content are excluded.
/// </summary>
internal static class BoilerplateFilter
{
    private static readonly Regex[] Patterns =
    [
        // Copyright lines: "© 2026 ..." / "(c) 2026 ..." / "Copyright 2026 ..."
        new(@"^\s*(©|\(c\)|copyright)\s*\d{4}", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*all rights reserved\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // "View in browser" / "Read on the web" / "View this email online"
        new(@"^\s*(view|read|see) (this )?(email |message |newsletter )?(in (your |a )?browser|on the web|web version|online)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*having (trouble|difficulty) (reading|viewing) this", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*if (you('re| are) )?having (trouble|difficulty)", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // Subscription / preferences footers
        new(@"^\s*unsubscribe\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*(update|manage|change) (your )?(email )?(preferences|subscription|settings)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*you('re| are) receiving this (email|message|newsletter)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*this (email|message) (was|is being) sent (to|from|by)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*(don'?t|do not) (want|wish) to receive", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*to (stop|opt out of|unsubscribe from) (receiving|these)", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // Reference / message IDs at line start
        new(@"^\s*(email|message|reference|tracking)\s+(reference\s+)?id\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // "Forward this email to a friend" / "Share this with..."
        new(@"^\s*(forward|share) this (email|message|newsletter)", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // Privacy / terms link rows (only when they're the entire line)
        new(@"^\s*(privacy policy|terms of (service|use)|cookie policy)\s*[|·•\-]?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    public static string Apply(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var lines = s.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (IsBoilerplate(line)) continue;
            kept.Add(line);
        }
        return string.Join('\n', kept);
    }

    private static bool IsBoilerplate(string line)
    {
        foreach (var p in Patterns)
        {
            if (p.IsMatch(line)) return true;
        }
        return false;
    }
}
