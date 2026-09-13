namespace Mailvec.Core.Attachments;

/// <summary>
/// An attachment's decoded content exceeded the ceiling its caller declared.
/// Deterministic for a given file + part, so callers should treat it as a
/// terminal answer for that document rather than something to retry.
/// Thrown by the parser (which does the bounded decode) and caught in Core and
/// the MCP tools — hence a Contracts type.
/// </summary>
public sealed class AttachmentTooLargeException(string describe, long limitBytes)
    : Exception($"{describe} exceeds the {limitBytes / (1024 * 1024)} MB limit for this operation and was not decoded.")
{
    /// <summary>The ceiling that was exceeded, in bytes.</summary>
    public long LimitBytes { get; } = limitBytes;
}
