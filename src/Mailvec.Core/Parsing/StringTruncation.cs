namespace Mailvec.Core.Parsing;

/// <summary>
/// The single snippet-truncation helper for every surface (search snippets,
/// tray previews, thread summaries). Slicing a string at a raw UTF-16 index
/// (<c>s[..240]</c>) can split a surrogate pair, leaving a lone high
/// surrogate at the tail; System.Text.Json then emits U+FFFD and the snippet
/// ends in garbage on any emoji-adjacent boundary. Backing off one unit when
/// the cut lands mid-pair keeps the output valid Unicode.
/// </summary>
public static class StringTruncation
{
    /// <summary>
    /// Truncates to at most <paramref name="maxChars"/> UTF-16 units and
    /// appends an ellipsis, never splitting a surrogate pair. Strings within
    /// the limit are returned unchanged.
    /// </summary>
    public static string Truncate(string s, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 1);
        if (s.Length <= maxChars) return s;

        var cut = maxChars;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut] + "…";
    }
}
