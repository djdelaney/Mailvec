namespace Mailvec.Core.Search;

/// <summary>
/// Optional filters applied to keyword, vector, and hybrid search.
/// All fields are AND-ed together; null fields are ignored.
/// </summary>
/// <param name="Folder">Exact folder name match (e.g. "INBOX", "Archive.2023").</param>
/// <param name="DateFrom">Inclusive lower bound on messages.date_sent.</param>
/// <param name="DateTo">Inclusive upper bound on messages.date_sent.</param>
/// <param name="FromContains">
/// Case-insensitive substring match against from_address OR from_name. Useful
/// for queries like "from bartlett" without needing the full email. Mutually
/// exclusive with <see cref="FromExact"/>; if both are set, FromExact wins.
/// </param>
/// <param name="FromExact">
/// Case-insensitive exact-match against from_address (only — names aren't
/// unique enough for an "exact" filter to mean anything useful). Set this for
/// "all email from invoice@anthropic.com" lookups.
/// </param>
public sealed record SearchFilters(
    string? Folder = null,
    DateTimeOffset? DateFrom = null,
    DateTimeOffset? DateTo = null,
    string? FromContains = null,
    string? FromExact = null)
{
    public static readonly SearchFilters None = new();

    public bool IsEmpty =>
        Folder is null && DateFrom is null && DateTo is null
        && string.IsNullOrEmpty(FromContains) && string.IsNullOrEmpty(FromExact);
}
