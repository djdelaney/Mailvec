using System.Globalization;
using System.Text;

namespace Mailvec.Core.Parsing;

/// <summary>
/// Final-pass cleanup applied to both V1 and V2 HTML-to-text output. Strips
/// invisible/zero-width characters (marketing preheader padding leans heavily
/// on these), normalizes non-breaking spaces, collapses inline whitespace,
/// trims each line, and limits blank lines to one between paragraphs.
/// </summary>
internal static class TextNormalize
{
    public static string Apply(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;

        // Pass 1: strip invisible chars, normalize NBSP-like to ASCII space.
        var stripped = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (IsInvisible(ch)) continue;
            stripped.Append(IsNbspLike(ch) ? ' ' : ch);
        }

        // Pass 2: collapse runs of inline whitespace to a single space, keep newlines.
        var collapsed = new StringBuilder(stripped.Length);
        bool prevWasSpace = false;
        foreach (var ch in stripped.ToString())
        {
            if (ch == '\n')
            {
                collapsed.Append('\n');
                prevWasSpace = false;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (!prevWasSpace) { collapsed.Append(' '); prevWasSpace = true; }
            }
            else
            {
                collapsed.Append(ch);
                prevWasSpace = false;
            }
        }

        // Pass 3: trim each line, collapse runs of blank lines to one.
        var result = new StringBuilder(collapsed.Length);
        int consecutiveBlanks = 0;
        foreach (var lineRaw in collapsed.ToString().Split('\n'))
        {
            var line = lineRaw.Trim();
            if (line.Length == 0)
            {
                consecutiveBlanks++;
                if (consecutiveBlanks == 1) result.Append('\n');
                continue;
            }
            consecutiveBlanks = 0;
            result.Append(line).Append('\n');
        }
        return result.ToString().Trim();
    }

    // UnicodeCategory.Format catches most zero-width formatting chars
    // (​ ZWSP, ‌ ZWNJ, ‍ ZWJ, ⁠ WJ, ﻿ BOM, ­ SHY).
    // The combining grapheme joiner ͏ is category NonSpacingMark; we add
    // it explicitly because marketing emails use it as visible-looking padding.
    private static bool IsInvisible(char c)
    {
        if (c == '͏') return true;
        return CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;
    }

    private static bool IsNbspLike(char c) =>
        c == ' ' ||  // non-breaking space
        c == ' ' ||  // figure space
        c == ' ';    // narrow no-break space
}
