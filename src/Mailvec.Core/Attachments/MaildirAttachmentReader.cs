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
    public AttachmentData Read(Message message, int partIndex, long? maxBytes)
    {
        ArgumentNullException.ThrowIfNull(message);

        var maildirFile = ResolveExistingSource(message);

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
        return new AttachmentData(entity, Decode(entity, maxBytes, DescribeFor(entity, partIndex)));
    }

    /// <summary>Decoded bytes of attachment <paramref name="partIndex"/> (no entity metadata).</summary>
    /// <param name="maxBytes">See <see cref="Read"/> — no default, on purpose.</param>
    public byte[] ReadBytes(Message message, int partIndex, long? maxBytes) =>
        Read(message, partIndex, maxBytes).Bytes;

    /// <summary>
    /// Resolve the source and confirm it is still there, throwing exactly what
    /// <see cref="Read"/> would — without parsing or decoding anything.
    /// </summary>
    /// <remarks>
    /// For callers that can answer from stored metadata and skip the read
    /// (view_attachment's summary-only path). Skipping the read must not also
    /// skip the containment guard or the "the .eml is gone" answer: a confident
    /// summary for a message whose file has vanished reports an attachment as
    /// present when it isn't, and would quietly retire the disclosure-controlled
    /// FileNotFoundException that the MCP tools translate for the client. One
    /// stat() buys back both.
    /// </remarks>
    public void EnsureSourceExists(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ResolveExistingSource(message);
    }

    /// <summary>
    /// The resolved, guarded, confirmed-present Maildir path for this message.
    /// </summary>
    private string ResolveExistingSource(Message message)
    {
        var maildirFile = ResolveMaildirFile(message);
        if (!File.Exists(maildirFile))
        {
            // The MESSAGE must stay free of the resolved path and the RFC
            // Message-ID: both MCP attachment tools surface FileNotFoundException
            // by rethrowing ex.Message as an McpException, which becomes the
            // JSON-RPC error string sent to the client — off-box, and through
            // Anthropic on the remote-connector deployment. Leaking the archive's
            // filesystem layout there would undo the same disclosure control
            // Mcp:RestrictHealthToLoopback exists to enforce.
            //
            // The path still travels, on FileName, where local consumers can log
            // it (the CLI backfill and the embedder's OCR pass both run on-box
            // and legitimately need to know which file is missing). Keep new
            // detail on properties, never in Message.
            throw new FileNotFoundException(
                $"Source message {message.Id} is no longer available at its recorded location. " +
                "It was probably moved or deleted; an indexer rescan should fix it.",
                maildirFile);
        }
        return maildirFile;
    }

    /// <summary>
    /// A name for the part, for the too-large message only. Falls back to the
    /// part index rather than anything derived from the message, keeping the
    /// exception text free of the Message-ID and the resolved path for the same
    /// disclosure reason as the FileNotFoundException above.
    /// </summary>
    private static string DescribeFor(MimeEntity entity, int partIndex)
    {
        var name = entity.ContentDisposition?.FileName ?? entity.ContentType?.Name;
        return string.IsNullOrWhiteSpace(name) ? $"the attachment at partIndex {partIndex}" : $"'{Path.GetFileName(name)}'";
    }

    private string ResolveMaildirFile(Message message) =>
        ResolveWithinRoot(_maildirRoot, message.MaildirPath, message.MaildirFilename);

    /// <summary>
    /// The containment guard on its own: combine <paramref name="maildirRoot"/>
    /// with a stored <c>maildir_path</c> / <c>maildir_filename</c> pair and
    /// refuse to hand back anything outside the root. Does not check existence.
    /// </summary>
    /// <remarks>
    /// Public and static because the guard has to be reachable from callers that
    /// read the whole <c>.eml</c> rather than one part, and so can't go through
    /// <see cref="Read"/> — the <c>mailvec extract-attachments</c> and
    /// <c>backfill-inline-images</c> CLI backfills. Both used to build the path
    /// with a bare <c>Path.Combine</c> and open it directly, which quietly
    /// exempted them from the invariant stated below. Any new Maildir read must
    /// come through here; a bare Path.Combine on those columns is the bug.
    /// </remarks>
    public static string ResolveWithinRoot(string maildirRoot, string maildirPath, string maildirFilename)
    {
        // maildir_path looks like "INBOX/cur" — relative to MaildirRoot, with
        // '/' separators that Path.Combine handles fine on macOS.
        var relative = maildirPath.Replace('/', Path.DirectorySeparatorChar);
        var canonicalRoot = Path.GetFullPath(maildirRoot);
        var target = Path.GetFullPath(Path.Combine(canonicalRoot, relative, maildirFilename));

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

        // Hand back the RESOLVED path, not the lexical one — the caller opens
        // whatever this returns, and returning `target` meant checking one path
        // and opening another. An actor who can write into the Maildir between
        // the check above and the caller's open could swap a directory
        // component for a symlink escaping the root; opening realTarget means
        // the open follows the chain that was actually verified.
        //
        // This NARROWS the race, it does not close it: a component of
        // realTarget could still become a symlink before the open. Closing it
        // properly needs openat/O_NOFOLLOW, which .NET doesn't expose. But the
        // remaining move is "delete a real directory and replace it with a
        // symlink mid-race" rather than "swap a leaf", which is far harder and
        // far noisier. Under the current threat model (only mbsync and the
        // indexer write here) neither is reachable; this is for the wider trust
        // model the symlink check above already anticipates.
        //
        // Side effect worth knowing: paths in logs and in the
        // FileNotFoundException below are now physically resolved, so a
        // symlinked Maildir root (~/Mail -> a volume) reports the volume path.
        // That is more accurate, but it is a visible change.
        return realTarget;
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

        var link = LinkTargetOf(combined);
        if (link is not null)
        {
            var next = Path.IsPathRooted(link) ? link : Path.Combine(realParent, link);
            return RealPath(next, linkHops + 1);
        }
        return combined;
    }

    /// <summary>
    /// The immediate symlink target of <paramref name="path"/>, or null when it
    /// is definitively not a link.
    /// </summary>
    /// <remarks>
    /// Throws when link status cannot be established, and that is the point.
    /// This used to swallow every exception and return null — i.e. "not a
    /// link" — which fails OPEN in a containment guard: an unreadable or
    /// erroring path silently skipped symlink resolution, leaving only the
    /// lexical check, which by construction cannot catch a symlinked component
    /// pointing outside the Maildir root. "Couldn't tell" must not resolve to
    /// "safe" here. A genuine non-link returns null without throwing (readlink
    /// on a regular file is not an error path in .NET), so this does not fire
    /// in normal operation; if it ever does, the read fails loudly instead of
    /// quietly widening the guard.
    /// </remarks>
    internal static string? LinkTargetOf(string path)
    {
        try
        {
            // LinkTarget is a path-based readlink; pick the info type that
            // matches what's on disk so a directory symlink resolves too.
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Cannot determine whether '{path}' is a symbolic link, so the Maildir containment guard " +
                "cannot be evaluated. Refusing the read.", ex);
        }
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

/// <summary>
/// An attachment's decoded content exceeded the ceiling its caller declared.
/// Deterministic for a given file + part, so callers should treat it as a
/// terminal answer for that document rather than something to retry.
/// </summary>
public sealed class AttachmentTooLargeException(string describe, long limitBytes)
    : Exception($"{describe} exceeds the {limitBytes / (1024 * 1024)} MB limit for this operation and was not decoded.")
{
    /// <summary>The ceiling that was exceeded, in bytes.</summary>
    public long LimitBytes { get; } = limitBytes;
}

/// <summary>An attachment's MIME entity plus its decoded bytes.</summary>
public sealed record AttachmentData(MimeEntity Entity, byte[] Bytes);
