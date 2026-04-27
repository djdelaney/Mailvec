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
}
