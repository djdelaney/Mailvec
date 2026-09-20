using Mailvec.Core.Models;

namespace Mailvec.Core.Parsing;

/// <summary>
/// What the parser hands back for a whole message. Plain data by design — it
/// is the record that crosses the parser boundary (see
/// <c>Mailvec.Parsing.Contracts.IMailParser</c>), so nothing here may hold a
/// MimeKit type.
/// </summary>
public sealed record ParsedMessage(
    string MessageId,
    string ThreadId,
    string? Subject,
    string? FromAddress,
    string? FromName,
    IReadOnlyList<EmailAddress> ToAddresses,
    IReadOnlyList<EmailAddress> CcAddresses,
    DateTimeOffset? DateSent,
    string? BodyText,
    string? BodyHtml,
    string RawHeaders,
    long SizeBytes,
    string ContentHash,
    IReadOnlyList<ParsedAttachment> Attachments)
{
    public bool HasAttachments => Attachments.Count > 0;
}

public sealed record ParsedAttachment(
    int PartIndex,
    string? FileName,
    string? ContentType,
    long? SizeBytes,
    string? ExtractedText = null,
    string? ExtractionStatus = null);
