using Mailvec.Core.Models;
using Mailvec.Core.Options;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailvec.Core.Attachments;

/// <summary>
/// Reads raw attachment bytes out of the Maildir <c>.eml</c> source. Owns the
/// security-sensitive path resolution (containment guard) and MIME decode so
/// the two readers of attachment bytes share one implementation: the MCP
/// <see cref="AttachmentExtractor"/> (which then writes the file to disk) and
/// the embedder's scanned-PDF OCR pass (which renders + OCRs in memory). Depends
/// only on the Maildir root. Like the extractor, this is a Maildir-touching path
/// — keep it out of any code that shouldn't read the filesystem.
/// </summary>
public sealed class MaildirAttachmentReader(IOptions<IngestOptions> ingest)
{
    private readonly string _maildirRoot = PathExpansion.Expand(ingest.Value.MaildirRoot);

    /// <summary>
    /// Resolve the Maildir source, load it, and return the attachment entity at
    /// <paramref name="partIndex"/> together with its decoded bytes. Throws
    /// <see cref="FileNotFoundException"/> when the source is missing (likely a
    /// stale DB row — an indexer rescan fixes it) and
    /// <see cref="ArgumentOutOfRangeException"/> when the part doesn't exist.
    /// </summary>
    public AttachmentData Read(Message message, int partIndex)
    {
        ArgumentNullException.ThrowIfNull(message);

        var maildirFile = ResolveMaildirFile(message);
        if (!File.Exists(maildirFile))
        {
            throw new FileNotFoundException(
                $"Maildir file not found for message {message.Id} ({message.MessageId}). " +
                $"The file may have been moved or deleted; an indexer rescan should fix it. " +
                $"Looked at: {maildirFile}");
        }

        using var stream = File.OpenRead(maildirFile);
        var mime = MimeMessage.Load(stream);

        // MessageParts.Indexable — not mime.Attachments — so inline (cid:) image
        // part_indexes resolve to bytes. Must match MessageParser's enumeration.
        var parts = MessageParts.Indexable(mime);
        if (partIndex < 0 || partIndex >= parts.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(partIndex),
                $"Message {message.Id} has {parts.Count} indexable part(s); partIndex {partIndex} is out of range.");
        }

        var entity = parts[partIndex];
        // MimeMessage.Load parses content into memory, so the entity (and its
        // decoded bytes) stay valid after the file stream closes.
        return new AttachmentData(entity, Decode(entity));
    }

    /// <summary>Decoded bytes of attachment <paramref name="partIndex"/> (no entity metadata).</summary>
    public byte[] ReadBytes(Message message, int partIndex) => Read(message, partIndex).Bytes;

    private string ResolveMaildirFile(Message message)
    {
        // maildir_path looks like "INBOX/cur" — relative to MaildirRoot, with
        // '/' separators that Path.Combine handles fine on macOS.
        var relative = message.MaildirPath.Replace('/', Path.DirectorySeparatorChar);
        var canonicalRoot = Path.GetFullPath(_maildirRoot);
        var target = Path.GetFullPath(Path.Combine(canonicalRoot, relative, message.MaildirFilename));

        // Containment guard — the path is built from DB columns, which are only
        // ever written by the trusted indexer (via Path.GetRelativePath). This
        // makes that invariant local: refuse to read outside the Maildir root
        // even if a future writer lets a traversal sequence into those columns.
        if (!target.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && target != canonicalRoot)
        {
            throw new InvalidOperationException(
                $"Refusing to read outside Maildir root. Target '{target}' is not within '{canonicalRoot}'.");
        }

        return target;
    }

    private static byte[] Decode(MimeEntity entity)
    {
        using var ms = new MemoryStream();
        if (entity is MimePart part && part.Content is not null)
        {
            part.Content.DecodeTo(ms);
        }
        else
        {
            // Multipart attachments (rare — e.g. message/rfc822 subparts).
            entity.WriteTo(ms);
        }
        return ms.ToArray();
    }
}

/// <summary>An attachment's MIME entity plus its decoded bytes.</summary>
public sealed record AttachmentData(MimeEntity Entity, byte[] Bytes);
