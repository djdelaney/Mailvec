namespace Mailvec.Core.Models;

public sealed record Message
{
    public long Id { get; init; }
    public required string MessageId { get; init; }
    public string? ThreadId { get; init; }
    public required string MaildirPath { get; init; }
    public required string MaildirFilename { get; init; }
    public required string Folder { get; init; }
    public string? Subject { get; init; }
    public string? FromAddress { get; init; }
    public string? FromName { get; init; }
    public IReadOnlyList<EmailAddress> ToAddresses { get; init; } = [];
    public IReadOnlyList<EmailAddress> CcAddresses { get; init; } = [];
    public DateTimeOffset? DateSent { get; init; }
    public DateTimeOffset? DateReceived { get; init; }
    public long SizeBytes { get; init; }
    public bool HasAttachments { get; init; }
    public IReadOnlyList<Attachment> Attachments { get; init; } = [];
    public string? BodyText { get; init; }
    public string? BodyHtml { get; init; }
    public string? RawHeaders { get; init; }
    public DateTimeOffset IndexedAt { get; init; }
    public DateTimeOffset? EmbeddedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
}
