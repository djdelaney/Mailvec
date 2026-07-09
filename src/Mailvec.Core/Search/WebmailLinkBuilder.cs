using Mailvec.Core.Options;

namespace Mailvec.Core.Search;

/// <summary>
/// Turns a stored Message-ID header into a Fastmail webmail deep-link via the
/// `msgid:<id>` search operator. The link drops the user on Fastmail's search
/// results pane with the single matching message highlighted — one extra
/// click vs. opening the conversation directly, but no JMAP token needed.
///
/// Returns null when AccountId isn't configured; tools should pass that null
/// through to clients (the field is nullable in every response shape).
/// </summary>
public static class WebmailLinkBuilder
{
    public static string? Build(string? rfcMessageId, FastmailOptions opts)
    {
        if (string.IsNullOrWhiteSpace(rfcMessageId)) return null;
        if (string.IsNullOrWhiteSpace(opts.AccountId)) return null;

        // Message-IDs may contain '@', '.', '+', and other chars; URL-escape to
        // be safe across Fastmail URL parsers and any reverse-proxy in the path.
        var encoded = Uri.EscapeDataString(rfcMessageId);
        var baseUrl = (opts.WebUrl ?? "https://app.fastmail.com").TrimEnd('/');
        return $"{baseUrl}/mail/search:msgid:{encoded}?u={Uri.EscapeDataString(opts.AccountId)}";
    }

    /// <summary>
    /// A ready-to-render Markdown link for a message — <c>[subject](url)</c> —
    /// with the subject's Markdown-significant characters escaped. The subject is
    /// an attacker-controlled email header, so the link is assembled here rather
    /// than by the model from a raw subject: that closes a link-spoofing vector
    /// where a crafted subject like <c>Invoice](https://evil.com) [x</c> renders
    /// as a link whose visible text looks benign but whose target is the
    /// attacker's site. Returns null whenever <paramref name="webmailUrl"/> is
    /// null (i.e. no configured account id), matching <see cref="Build"/>.
    /// The URL is already percent-encoded by <see cref="Build"/>, so its parens
    /// can't terminate the <c>(...)</c> target early.
    /// </summary>
    public static string? MarkdownLink(string? webmailUrl, string? subject)
    {
        if (string.IsNullOrEmpty(webmailUrl)) return null;
        var text = string.IsNullOrWhiteSpace(subject) ? "(no subject)" : EscapeLinkText(subject);
        return $"[{text}]({webmailUrl})";
    }

    // Escape the characters that would let text break out of the [...] link
    // span. Backslash first so we don't double-escape our own escapes; then the
    // brackets that open/close the span; finally collapse CR/LF to a space so an
    // embedded newline can't split the link across lines.
    private static string EscapeLinkText(string text) => text
        .Replace("\\", "\\\\")
        .Replace("[", "\\[")
        .Replace("]", "\\]")
        .Replace("\r", " ")
        .Replace("\n", " ");
}
