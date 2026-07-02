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
        // Lexical check first (fast, catches ../).
        if (!IsWithin(target, canonicalRoot))
        {
            throw new InvalidOperationException(
                $"Refusing to read outside Maildir root. Target '{target}' is not within '{canonicalRoot}'.");
        }

        // Then a symlink-resolved check: a symlinked directory/file component
        // inside the Maildir could point outside the root and still pass the
        // lexical check. Defense-in-depth today (trusted writer), but it makes
        // the guard real before any wider trust model (remote/container).
        var realRoot = RealPath(canonicalRoot);
        var realTarget = RealPath(target);
        if (!IsWithin(realTarget, realRoot))
        {
            throw new InvalidOperationException(
                $"Refusing to read outside Maildir root (symlink-resolved). Target '{realTarget}' is not within '{realRoot}'.");
        }

        return target;
    }

    private static bool IsWithin(string path, string root) =>
        path == root || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// Resolve symlinks along <paramref name="path"/> so the containment check
    /// can't be bypassed by a symlinked component pointing outside the root.
    /// .NET has no <c>realpath(3)</c>, so resolve component-by-component: the
    /// real parent plus the leaf, following the leaf when it's a link.
    /// <paramref name="linkHops"/> caps symlink-loop recursion (plain directory
    /// descent doesn't count toward it).
    /// </summary>
    private static string RealPath(string path, int linkHops = 0)
    {
        if (linkHops > 40)
            throw new InvalidOperationException("Too many symbolic links while resolving a Maildir path.");

        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        if (parent is null)
            return full; // filesystem root

        var realParent = RealPath(parent, linkHops);
        var combined = Path.Combine(realParent, Path.GetFileName(full));

        var link = SafeLinkTarget(combined);
        if (link is not null)
        {
            var next = Path.IsPathRooted(link) ? link : Path.Combine(realParent, link);
            return RealPath(next, linkHops + 1);
        }
        return combined;
    }

    /// <summary>The immediate symlink target of <paramref name="path"/>, or null if it isn't a link (or can't be read).</summary>
    private static string? SafeLinkTarget(string path)
    {
        try
        {
            // LinkTarget is a path-based readlink; pick the info type that
            // matches what's on disk so a directory symlink resolves too.
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget;
        }
        catch
        {
            return null;
        }
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
