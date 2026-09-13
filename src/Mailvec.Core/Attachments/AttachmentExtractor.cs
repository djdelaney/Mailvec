using System.Text;
using Mailvec.Core.Models;
using Mailvec.Core.Options;
using Mailvec.Parsing.Contracts;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Attachments;

/// <summary>
/// The Core-side facade over "which file" (<see cref="MaildirAttachmentReader"/>:
/// path resolution, containment guard, existence) and "what is inside it"
/// (<see cref="IMailParser"/>: MIME decode, rasterisation). Two entry points:
///
/// <list type="bullet">
/// <item><see cref="ExtractInMemory"/> returns the decoded bytes + metadata and
/// touches no disk — the path the read-only MCP tools use to inline an image /
/// small text file. Nothing is persisted.</item>
/// <item><see cref="Extract"/> additionally writes the bytes to a user-visible
/// download directory (~/Downloads/mailvec/ by default) and returns the path.
/// Reserved for the explicit, user-initiated download path —
/// <c>mailvec extract-attachments</c> — not
/// the automatic agent read path, so ordinary searches never litter mail content
/// on disk.</item>
/// </list>
///
/// The MCP viewer tools get their rasterised output through here too
/// (<see cref="RenderPdfPages"/>, <see cref="NormalizeImage"/>), so no tool
/// holds a reader and a parser of its own. This is (with the reader) the only
/// place outside the indexer that reads from the Maildir, so it owns the small
/// architectural break of "MCP must know MaildirRoot".
/// </summary>
public sealed class AttachmentExtractor(
    IOptions<IngestOptions> ingestOptions,
    IOptions<McpOptions> mcpOptions,
    IMailParser parser)
{
    private readonly MaildirAttachmentReader _reader = new(ingestOptions);
    private readonly string _downloadDir = PathExpansion.Expand(mcpOptions.Value.AttachmentDownloadDir);
    private readonly int _inlineTextMaxBytes = mcpOptions.Value.AttachmentInlineTextMaxBytes;

    public string DownloadDir => _downloadDir;

    /// <summary>
    /// Confirm the Maildir source is still present, without decoding anything.
    /// See <see cref="MaildirAttachmentReader.EnsureSourceExists"/>.
    /// </summary>
    public void EnsureSourceExists(Message message) => _reader.EnsureSourceExists(message);

    /// <summary>
    /// Decode the attachment at <paramref name="partIndex"/> entirely in memory —
    /// no bytes are written to disk. Returns the resolved filename / content type /
    /// size, the decoded bytes, and (for small text-ish files) the decoded UTF-8
    /// text. Throws <see cref="FileNotFoundException"/> when the Maildir source is
    /// missing and <see cref="ArgumentOutOfRangeException"/> when the part doesn't
    /// exist — same as <see cref="Extract"/>.
    /// </summary>
    /// <param name="maxBytes">
    /// Ceiling on the decoded size, or null for none — see
    /// <see cref="IMailParser.DecodePart"/>. Throws
    /// <see cref="AttachmentTooLargeException"/> above it.
    /// </param>
    public InlineAttachment ExtractInMemory(Message message, int partIndex, long? maxBytes)
    {
        ArgumentNullException.ThrowIfNull(message);

        var part = parser.DecodePart(_reader.ReadEml(message), partIndex, maxBytes);
        var inlineText = TryDecodeInlineText(part.Bytes, part.ContentType);

        return new InlineAttachment(
            FileName: part.FileName,
            ContentType: part.ContentType,
            SizeBytes: part.Bytes.LongLength,
            Bytes: part.Bytes,
            InlineText: inlineText);
    }

    /// <summary>Resolved name and type of a part, without decoding it. Same exceptions as <see cref="ExtractInMemory"/>.</summary>
    public PartInfo Describe(Message message, int partIndex)
    {
        ArgumentNullException.ThrowIfNull(message);
        return parser.DescribePart(_reader.ReadEml(message), partIndex);
    }

    /// <summary>Rasterise PDF pages of a part — see <see cref="IMailParser.RenderPdfPages"/>.</summary>
    public PdfRender RenderPdfPages(Message message, int partIndex, int firstPage, int maxPages, long? maxBytes)
    {
        ArgumentNullException.ThrowIfNull(message);
        return parser.RenderPdfPages(_reader.ReadEml(message), partIndex, firstPage, maxPages, maxBytes);
    }

    /// <summary>Decode and normalise an image part for vision — see <see cref="IMailParser.NormalizeImage"/>.</summary>
    public Pdf.NormalizedImage? NormalizeImage(Message message, int partIndex, long? maxBytes)
    {
        ArgumentNullException.ThrowIfNull(message);
        return parser.NormalizeImage(_reader.ReadEml(message), partIndex, maxBytes);
    }

    /// <summary>
    /// Decode the attachment at <paramref name="partIndex"/> and write the
    /// bytes to <see cref="DownloadDir"/>. The output filename is
    /// `{messageId}-{partIndex}-{sanitized-filename}` so collisions across
    /// messages are impossible and the originating email is greppable.
    ///
    /// If the target file already holds byte-for-byte the same content we skip
    /// rewriting (idempotent re-fetches are cheap) and set `wasReused` so
    /// callers can surface that fact. The comparison is over content, not
    /// length — see <see cref="TryReuseExisting"/> for why length was wrong.
    ///
    /// Throws <see cref="FileNotFoundException"/> when the Maildir source is
    /// missing (likely a stale DB row — an indexer rescan should fix it) and
    /// <see cref="ArgumentOutOfRangeException"/> when the requested part
    /// doesn't exist on the message.
    /// </summary>
    public ExtractResult Extract(Message message, int partIndex)
    {
        // No ceiling: this path only runs because a user explicitly asked for
        // the file (`mailvec extract-attachments`), and
        // refusing to save an attachment because it is large would be refusing
        // the thing they asked for. The inline/agent paths are where a ceiling
        // belongs, because there the size is nobody's decision.
        var att = ExtractInMemory(message, partIndex, maxBytes: null);

        // Prefix with message id + part index — guarantees no collisions across
        // emails that happened to attach files with the same name, and keeps
        // the originating email greppable from the saved filename.
        var outputName = $"{message.Id}-{partIndex}-{att.FileName}";
        var targetPath = ResolveSafeOutputPath(_downloadDir, outputName);

        bool wasReused = TryReuseExisting(targetPath, att.Bytes);
        if (!wasReused)
        {
            var dir = Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(dir);

            // Write to a sibling temp file then rename, so a concurrent reader
            // never sees a partial file at targetPath.
            //
            // The temp name is random AND created exclusively, because the old
            // `targetPath + ".part"` was neither. ResolveSafeOutputPath refuses
            // to write through a symlink at the TARGET, but nothing guarded the
            // sibling — and File.WriteAllBytes follows a symlink, so anything
            // able to pre-place `<target>.part` redirected the attachment bytes
            // to any path that user could write. FileMode.CreateNew maps to
            // open(O_CREAT|O_EXCL), which fails outright on an existing path
            // (including a dangling symlink) rather than following it, so even
            // a guessed name is refused rather than followed.
            var tempPath = Path.Combine(dir, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.part");
            try
            {
                using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(att.Bytes);
                }
                // rename(2) replaces the destination entry itself and does not
                // follow a symlink there, so the swap stays safe even if the
                // target check above raced.
                File.Move(tempPath, targetPath, overwrite: true);
            }
            catch
            {
                // Best-effort cleanup of the temp file on failure.
                try { File.Delete(tempPath); } catch (IOException) { }
                throw;
            }
        }

        return new ExtractResult(
            FilePath: targetPath,
            FileName: att.FileName,
            ContentType: att.ContentType,
            SizeBytes: att.SizeBytes,
            WasReused: wasReused,
            InlineText: att.InlineText);
    }

    /// <summary>
    /// Resolve a target path that's guaranteed to live inside <paramref name="downloadDir"/>.
    /// Lexical check (refuse paths that don't start with the canonicalized dir)
    /// plus a symlink check at the destination (refuse to overwrite an existing
    /// symlink, which could redirect the write outside the dir). Pattern
    /// borrowed from fastmail-mcp's safeWritePath.
    /// </summary>
    private static string ResolveSafeOutputPath(string downloadDir, string outputName)
    {
        if (string.IsNullOrEmpty(outputName) || outputName.Contains('\0'))
            throw new ArgumentException("Output name is empty or contains null bytes.", nameof(outputName));

        // outputName has already had directory components stripped by
        // AttachmentNaming.ResolveFileName, but defend in depth: refuse anything
        // with a separator or that resolves to a parent.
        if (outputName.Contains('/') || outputName.Contains('\\') || outputName == ".." || outputName.StartsWith(".."))
            throw new ArgumentException($"Output name '{outputName}' looks like a path component, not a filename.", nameof(outputName));

        Directory.CreateDirectory(downloadDir);
        var canonicalDir = Path.GetFullPath(downloadDir);
        var target = Path.GetFullPath(Path.Combine(canonicalDir, outputName));

        // Canonical path containment — final defence.
        if (!target.StartsWith(canonicalDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) && target != canonicalDir)
        {
            throw new InvalidOperationException(
                $"Refusing to write outside download dir. Target '{target}' is not within '{canonicalDir}'.");
        }

        // Refuse to write through an existing symlink at the target — even if
        // the lexical path is fine, a symlink could redirect to /etc/passwd or
        // similar. We just delete-and-rewrite normally for regular files.
        var info = new FileInfo(target);
        if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Refusing to overwrite existing symlink at '{target}'.");
        }

        return target;
    }

    /// <summary>
    /// True only when the file at <paramref name="path"/> is byte-for-byte the
    /// attachment we just decoded.
    /// </summary>
    /// <remarks>
    /// This used to compare length alone, on the reasoning that re-decoding
    /// from the Maildir dominates the cost anyway — which is true, and is
    /// exactly why the length shortcut bought nothing: the bytes are already in
    /// hand by the time we get here. What it cost was correctness.
    /// <c>WasReused=true</c> is reported to the caller as "this file IS the
    /// attachment", and two different payloads of equal length under the same
    /// message id + part index (an <c>.eml</c> rewritten post-ingest, then
    /// re-indexed) returned the stale file. A same-size unrelated file already
    /// sitting at the target was likewise adopted as the attachment.
    /// Comparing content makes the flag mean what it says.
    /// </remarks>
    private static bool TryReuseExisting(string path, byte[] expected)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expected.LongLength) return false;

        try
        {
            // Length already matches, so this reads exactly the attachment's
            // size — bounded by the same cap that bounded the decode.
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);
        }
        catch (IOException)
        {
            // Unreadable (locked, vanished mid-check) — fall through and
            // rewrite rather than claiming a cache hit we couldn't verify.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Name resolution from stored column values. Forwards to
    /// <see cref="AttachmentNaming"/>, which the parser also uses, so the
    /// row-based short-circuit in <c>view_attachment</c> produces the SAME name
    /// the file-reading path would.
    /// </summary>
    public static string ResolveFileName(string? rawName, string? declaredContentType, int partIndex) =>
        AttachmentNaming.ResolveFileName(rawName, declaredContentType, partIndex);

    /// <summary>Content-type resolution from a stored value — see <see cref="AttachmentNaming.ResolveContentType"/>.</summary>
    public static string ResolveContentType(string? declaredContentType, string fileName) =>
        AttachmentNaming.ResolveContentType(declaredContentType, fileName);

    /// <summary>See <see cref="AttachmentNaming.MimeForExtension"/>.</summary>
    internal static string? MimeForExtension(string ext) => AttachmentNaming.MimeForExtension(ext);

    private string? TryDecodeInlineText(byte[] bytes, string contentType)
    {
        if (_inlineTextMaxBytes <= 0 || bytes.Length > _inlineTextMaxBytes) return null;
        if (!IsTextLikeContentType(contentType)) return null;

        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <see cref="TryDecodeInlineText"/> would attempt this type at all.
    /// Public so view_attachment's pre-read check asks the same question.
    /// </summary>
    public static bool IsTextLikeContentType(string contentType) =>
        AttachmentNaming.IsTextLikeContentType(contentType);
}

public sealed record ExtractResult(
    string FilePath,
    string FileName,
    string ContentType,
    long SizeBytes,
    bool WasReused,
    string? InlineText);

/// <summary>An attachment decoded in memory: metadata + bytes, nothing on disk.</summary>
public sealed record InlineAttachment(
    string FileName,
    string ContentType,
    long SizeBytes,
    byte[] Bytes,
    string? InlineText);
