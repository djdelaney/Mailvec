namespace Mailvec.OutlookExport;

public sealed record ExportedAddress(string? Name, string Address);

public sealed record ExportedAttachment(string FileName, byte[] Data);

/// <summary>
/// Plain snapshot of everything we pull off an Outlook MailItem over COM.
/// Keeping this COM-free means <see cref="EmlBuilder"/> stays pure and
/// unit-testable on any platform.
/// </summary>
public sealed record ExportedMessage
{
    /// <summary>Always set: the real PR_INTERNET_MESSAGE_ID when present,
    /// otherwise a deterministic synthetic id derived from the EntryID so
    /// re-runs produce the same file name and Mailvec's Message-ID dedup
    /// still works.</summary>
    public required string MessageId { get; init; }

    public string? InReplyTo { get; init; }

    /// <summary>Raw References header value (whitespace-separated msg-ids).</summary>
    public string? References { get; init; }

    public string? FromName { get; init; }
    public string? FromAddress { get; init; }

    public IReadOnlyList<ExportedAddress> To { get; init; } = [];
    public IReadOnlyList<ExportedAddress> Cc { get; init; } = [];
    public IReadOnlyList<ExportedAddress> Bcc { get; init; } = [];

    public string Subject { get; init; } = "";
    public required DateTimeOffset Date { get; init; }

    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }

    public IReadOnlyList<ExportedAttachment> Attachments { get; init; } = [];
}
