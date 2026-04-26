namespace Mailvec.Core.Models;

public sealed record SearchHit(
    long MessageId,
    string MessageIdHeader,
    string Folder,
    string? Subject,
    string? FromAddress,
    string? FromName,
    DateTimeOffset? DateSent,
    string Snippet,
    double Bm25Score);
