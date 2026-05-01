using System.Security.Cryptography;
using MimeKit;

namespace Mailvec.Core.Parsing;

/// <summary>
/// SHA-256 of the parsed message body section (everything after the top-level
/// header block). Excludes headers so post-delivery rewrites that don't touch
/// content (DKIM verification stamps, X-Spam-Score, etc.) don't trigger
/// spurious re-embeds. Output is lowercase hex, 64 chars.
/// </summary>
public static class MessageBodyHasher
{
    public static string Hash(MimeMessage mime)
    {
        ArgumentNullException.ThrowIfNull(mime);

        // mime.Body is the top-level MimeEntity (the multipart wrapper for
        // most mail, or the bare body for single-part). WriteTo serializes
        // the entity verbatim, including its own MIME headers and any nested
        // boundaries. That's exactly what we want — it's stable across
        // arbitrary header rewrites at the message level but reflects any
        // change to the body bytes themselves.
        if (mime.Body is null) return EmptyHash;

        using var sha = SHA256.Create();
        using var sink = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write);
        mime.Body.WriteTo(sink);
        sink.FlushFinalBlock();
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>SHA-256 of the empty input. Stamped on messages whose body is null/missing.</summary>
    private static readonly string EmptyHash = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
}
