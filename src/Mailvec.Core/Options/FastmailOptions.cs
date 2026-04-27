namespace Mailvec.Core.Options;

/// <summary>
/// Optional config that lets Mailvec emit deep links into Fastmail's web UI
/// alongside search/get results. The current implementation uses Fastmail's
/// `msgid:` search-URL syntax (zero API calls, zero auth) — clicking the link
/// drops the user on a Fastmail search-results page with one hit. If
/// AccountId is left empty, no links are emitted (the feature is opt-in).
/// </summary>
public sealed class FastmailOptions
{
    public const string SectionName = "Fastmail";

    /// <summary>
    /// JMAP account id, format `u` + 8 hex chars. Find yours by logging into
    /// app.fastmail.com and copying the `?u=...` query param from any URL.
    /// Empty string disables webmail links.
    /// </summary>
    public string AccountId { get; set; } = "";

    /// <summary>
    /// Override for self-hosted Fastmail-API-compatible deployments (rare).
    /// Trailing slash is stripped at use time.
    /// </summary>
    public string WebUrl { get; set; } = "https://app.fastmail.com";
}
