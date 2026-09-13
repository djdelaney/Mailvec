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
        // Trademark + copyright lines: "TM and © 2026 Apple Inc.", "® and © ..."
        new(@"^\s*(®|™|tm)\s*(and\s*)?(©|\(c\))\s*\d{4}", RegexOptions.IgnoreCase | RegexOptions.Compiled),
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

        // Bullet/dot-separated short link rows.
        // Marketing emails often render footer-link strips as plain text once
        // HtmlToText drops their <a> wrappers — e.g. "Apple Account • Terms of
        // Sale • Privacy Policy" or "Help · Settings · Unsubscribe". Match
        // lines composed entirely of 2-4 short token groups separated by
        // bullet-like glyphs and nothing else.
        new(@"^\s*[A-Za-z][\w &]{0,29}(\s*[•·|]\s*[A-Za-z][\w &]{0,29}){1,4}\s*$", RegexOptions.Compiled),

        // Trailing-arrow nav links from receipt/account templates ("Purchase
        // History ›", "Report a Problem ›", "View Your Account Information ›").
        // Capped to 60 chars so we don't catch a real sentence that happens
        // to end with the same glyph.
        new(@"^\s*[\w][\w &./-]{1,58}\s*[›>]\s*$", RegexOptions.Compiled),

        // Apple receipt template — refund / cancellation / billing-question
        // boilerplate. Each pattern matches the line opener; the rest of the
        // line goes with it. Phrasing is specific enough not to false-positive
        // on real correspondence.
        new(@"^\s*if you have any questions about your (bill|order|subscription|payment|account)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*you may contact .{1,40} for a (full |partial )?refund", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*partial refunds are available", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*you can turn off (renewal|automatic|email)\s+(receipts?|payments?|emails?)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*turn off (renewal|automatic|email)\s+(receipts?|payments?|emails?)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*get help with (subscriptions|orders|purchases|your account|billing)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*view your receipts? anytime\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // Lines containing leftover template placeholders ("@@supportUrl@@",
        // "{{cancellation_link}}"). These are template-engine bugs from the
        // sender — never useful content, occasionally appear inside otherwise-
        // legitimate sentences (which is why we match anywhere on the line).
        new(@"@@\w+@@|\{\{\w+\}\}", RegexOptions.Compiled),
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
