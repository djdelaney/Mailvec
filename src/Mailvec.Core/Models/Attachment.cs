namespace Mailvec.Core.Models;

public sealed record Attachment(
    int PartIndex,
    string? FileName,
    string? ContentType,
    long? SizeBytes);
