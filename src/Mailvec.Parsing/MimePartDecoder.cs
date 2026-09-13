using Mailvec.Core.Attachments;
using Mailvec.Parsing.Contracts;
using MimeKit;

namespace Mailvec.Parsing;

/// <summary>
/// The "what is inside the file" half of what used to be
/// <c>MaildirAttachmentReader</c>: parse the <c>.eml</c> bytes, resolve a
/// <c>part_index</c> back to a MIME part through <see cref="MessageParts.Indexable"/>
/// (the ONE enumeration the writer, <see cref="Core.Parsing.MessageParser"/>,
/// also uses — if the two ever diverged, view_attachment and OCR would read the
/// wrong bytes), and decode it under a ceiling enforced during the decode.
/// The "which file" half — path resolution and the containment guard — stayed
/// in Core with the reader, because it is a caller decision.
/// </summary>
public static class MimePartDecoder
{
    public static MimeMessage Load(byte[] eml)
    {
        ArgumentNullException.ThrowIfNull(eml);
        using var stream = new MemoryStream(eml, writable: false);
        return MimeMessage.Load(stream);
    }

    /// <summary>
    /// The entity at <paramref name="partIndex"/>. Throws
    /// <see cref="ArgumentOutOfRangeException"/> for a part that doesn't exist.
    /// </summary>
    public static MimeEntity Locate(MimeMessage mime, int partIndex)
    {
        var parts = MessageParts.Indexable(mime);
        if (partIndex < 0 || partIndex >= parts.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(partIndex),
                $"The message has {parts.Count} indexable part(s); partIndex {partIndex} is out of range.");
        }
        return parts[partIndex];
    }

    public static PartInfo Describe(byte[] eml, int partIndex) =>
        Describe(Locate(Load(eml), partIndex), partIndex);

    public static PartInfo Describe(MimeEntity entity, int partIndex)
    {
        var fileName = AttachmentNaming.ResolveFileName(
            entity.ContentDisposition?.FileName ?? entity.ContentType?.Name,
            entity.ContentType?.MimeType, partIndex);
        var contentType = AttachmentNaming.ResolveContentType(entity.ContentType?.MimeType, fileName);
        return new PartInfo(fileName, contentType);
    }

    /// <param name="maxBytes">
    /// Ceiling on the DECODED size, or null for no ceiling. No default on
    /// purpose: the right answer differs per caller and none of them is
    /// obviously right for the others. A user who clicked Save asked for the
    /// whole file and gets null; the MCP tools inline into a protocol message
    /// and cap accordingly; the OCR pass is a background loop and caps hardest.
    /// A default would silently hand one caller's policy to the next one added.
    /// Over the ceiling this throws <see cref="AttachmentTooLargeException"/>
    /// mid-decode, so the bytes are never fully materialized.
    /// </param>
    public static DecodedPart Decode(byte[] eml, int partIndex, long? maxBytes)
    {
        // MimeMessage.Load parses content into memory, so the entity (and its
        // decoded bytes) stay valid after the source stream is gone.
        var entity = Locate(Load(eml), partIndex);
        var info = Describe(entity, partIndex);
        return new DecodedPart(info.FileName, info.ContentType, Decode(entity, maxBytes, DescribeFor(entity, partIndex)));
    }

    /// <summary>
    /// A name for the part, for the too-large message only. Falls back to the
    /// part index rather than anything derived from the message, keeping the
    /// exception text free of the Message-ID and any path — the MCP tools
    /// rethrow ex.Message to the client.
    /// </summary>
    private static string DescribeFor(MimeEntity entity, int partIndex)
    {
        var name = entity.ContentDisposition?.FileName ?? entity.ContentType?.Name;
        return string.IsNullOrWhiteSpace(name) ? $"the attachment at partIndex {partIndex}" : $"'{Path.GetFileName(name)}'";
    }

    /// <remarks>
    /// The cap is enforced DURING the decode, not by checking a size first.
    /// Content-Length and the stored size_bytes are both claims about the part,
    /// and a cap that trusts a claim isn't a cap. Writing through
    /// <see cref="BoundedStream"/> means an over-cap part costs one buffer past
    /// the limit and then throws, instead of one full-size MemoryStream (which
    /// grows by doubling, so up to 2x) plus the ToArray copy.
    /// </remarks>
    private static byte[] Decode(MimeEntity entity, long? maxBytes, string describe)
    {
        using var ms = new MemoryStream();
        using (var bounded = new BoundedStream(ms, maxBytes, describe))
        {
            if (entity is MimePart part && part.Content is not null)
            {
                part.Content.DecodeTo(bounded);
            }
            else
            {
                // Multipart attachments (rare — e.g. message/rfc822 subparts).
                entity.WriteTo(bounded);
            }
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Write-through wrapper that throws once more than <c>maxBytes</c> has been
    /// written. Leaves the inner stream open — the caller owns it.
    /// </summary>
    private sealed class BoundedStream(Stream inner, long? maxBytes, string describe) : Stream
    {
        private long _written;

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(new ReadOnlySpan<byte>(buffer, offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _written += buffer.Length;
            if (maxBytes is { } cap && _written > cap)
                throw new AttachmentTooLargeException(describe, cap);
            inner.Write(buffer);
        }

        public override void WriteByte(byte value) => Write([value]);

        public override void Flush() => inner.Flush();
        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
