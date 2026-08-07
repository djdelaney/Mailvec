using System.Text;
using Mailvec.Core.Models;
using Mailvec.Core.Options;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Mailvec.Core.Attachments;

/// <summary>
/// Decodes an attachment from its Maildir source file. Two entry points:
///
/// <list type="bullet">
/// <item><see cref="ExtractInMemory"/> returns the decoded bytes + metadata and
/// touches no disk — the path the read-only MCP tools use to inline an image /
/// small text file or rasterise a PDF page. Nothing is persisted.</item>
/// <item><see cref="Extract"/> additionally writes the bytes to a user-visible
/// download directory (~/Downloads/mailvec/ by default) and returns the path.
/// Reserved for the explicit, user-initiated download path —
/// <c>mailvec extract-attachments</c> — not
/// the automatic agent read path, so ordinary searches never litter mail content
/// on disk.</item>
/// </list>
///
/// This is (with <see cref="MaildirAttachmentReader"/>) the only place outside
/// the indexer that reads from the Maildir, so it owns the small architectural
/// break of "MCP must know MaildirRoot". See CLAUDE.md (Attachment-extraction
/// gotchas) for the rationale.
/// </summary>
public sealed class AttachmentExtractor(
    IOptions<IngestOptions> ingestOptions,
    IOptions<McpOptions> mcpOptions)
{
    private readonly MaildirAttachmentReader _reader = new(ingestOptions);
    private readonly string _downloadDir = PathExpansion.Expand(mcpOptions.Value.AttachmentDownloadDir);
    private readonly int _inlineTextMaxBytes = mcpOptions.Value.AttachmentInlineTextMaxBytes;

    private static readonly HashSet<string> InlineTextContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json", "application/xml", "application/yaml", "application/x-yaml",
        "application/javascript", "application/x-sh", "application/sql",
        "application/csv", "application/x-csv",
    };

    public string DownloadDir => _downloadDir;

    /// <summary>
    /// Confirm the Maildir source is still present, without decoding anything.
    /// See <see cref="MaildirAttachmentReader.EnsureSourceExists"/>.
    /// </summary>
    public void EnsureSourceExists(Message message) => _reader.EnsureSourceExists(message);

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
    /// <see cref="MaildirAttachmentReader.Read"/>. Throws
    /// <see cref="AttachmentTooLargeException"/> above it.
    /// </param>
    public InlineAttachment ExtractInMemory(Message message, int partIndex, long? maxBytes)
    {
        ArgumentNullException.ThrowIfNull(message);

        var data = _reader.Read(message, partIndex, maxBytes);
        var entity = data.Entity;
        var safeName = ResolveSafeFileName(entity, partIndex);
        var contentType = ResolveContentType(entity, safeName);
        var inlineText = TryDecodeInlineText(data.Bytes, contentType);

        return new InlineAttachment(
            FileName: safeName,
            ContentType: contentType,
            SizeBytes: data.Bytes.LongLength,
            Bytes: data.Bytes,
            InlineText: inlineText);
    }

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
        // ResolveSafeFileName, but defend in depth: refuse anything with a
        // separator or that resolves to a parent.
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
    /// Pulls a safe filename out of the MIME entity. Strips path separators
    /// (a malicious / careless filename like "../../etc/passwd" could otherwise
    /// land outside the download directory). Falls back to a synthesized name
    /// when the part has no Content-Disposition filename / Content-Type name.
    /// </summary>
    private static string ResolveSafeFileName(MimeEntity entity, int partIndex) =>
        ResolveFileName(entity.ContentDisposition?.FileName ?? entity.ContentType?.Name,
            entity.ContentType?.MimeType, partIndex);

    /// <summary>
    /// The same resolution as <see cref="ResolveSafeFileName"/>, from stored
    /// column values instead of a live MIME entity.
    /// </summary>
    /// <remarks>
    /// Public because <c>view_attachment</c> decides from the attachments row
    /// whether a part could be inlined at all, and only opens the Maildir when
    /// the answer might be yes. That decision has to produce the SAME name and
    /// type the file-reading path would, or the short-circuit changes behaviour
    /// instead of just skipping work — so both paths route through here rather
    /// than through two lookalike implementations.
    /// </remarks>
    public static string ResolveFileName(string? rawName, string? declaredContentType, int partIndex)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return $"attachment-{partIndex}{ExtensionFromContentType(declaredContentType)}";

        var safe = Path.GetFileName(rawName).Replace('\0', '_').Trim();
        return string.IsNullOrEmpty(safe)
            ? $"attachment-{partIndex}{ExtensionFromContentType(declaredContentType)}"
            : safe;
    }

    /// <summary>
    /// Resolve the most specific content type we can. Many mail clients attach
    /// PDFs / docs / images with `Content-Type: application/octet-stream` and
    /// rely on the filename extension for type info. The text response and
    /// image-detection branch both benefit from a real MIME, so substitute
    /// when we recognise the extension.
    /// </summary>
    private static string ResolveContentType(MimeEntity entity, string fileName) =>
        ResolveContentType(entity.ContentType?.MimeType, fileName);

    /// <summary>
    /// The same resolution from a stored content_type value. Public for the
    /// same reason as <see cref="ResolveFileName"/> — and load-bearing for it:
    /// mail clients routinely send PDFs and photos as
    /// <c>application/octet-stream</c>, so a short-circuit reading the raw
    /// column would classify a JPEG as un-inlineable binary and quietly stop
    /// showing images that work today.
    /// </summary>
    public static string ResolveContentType(string? declaredContentType, string fileName)
    {
        var declared = declaredContentType;
        if (string.IsNullOrEmpty(declared)) declared = "application/octet-stream";

        var isGeneric = string.Equals(declared, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(declared, "binary/octet-stream", StringComparison.OrdinalIgnoreCase);
        if (!isGeneric) return declared;

        var fromExt = MimeFromExtension(fileName);
        return fromExt ?? declared;
    }

    private static string? MimeFromExtension(string fileName) =>
        MimeForExtension(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>
    /// Known MIME for a lowercase filename extension including the leading dot
    /// ('.pdf'), or null. Also consumed by SearchFilterSql's attachmentType
    /// filter so "pdf" matches correctly-typed attachments with odd filenames.
    /// </summary>
    internal static string? MimeForExtension(string ext) => ext switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".heic" => "image/heic",
        ".tiff" or ".tif" => "image/tiff",
        ".bmp" => "image/bmp",
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".html" or ".htm" => "text/html",
        ".xml" => "application/xml",
        ".json" => "application/json",
        ".yaml" or ".yml" => "application/yaml",
        ".md" => "text/markdown",
        ".zip" => "application/zip",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".doc" => "application/msword",
        ".xls" => "application/vnd.ms-excel",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".wav" => "audio/wav",
        _ => null,
    };

    private static string ExtensionFromContentType(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "application/pdf" => ".pdf",
        "application/zip" => ".zip",
        "application/json" => ".json",
        "application/xml" or "text/xml" => ".xml",
        "text/plain" => ".txt",
        "text/csv" or "application/csv" or "application/x-csv" => ".csv",
        "text/html" => ".html",
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        _ => string.Empty,
    };

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
    public static bool IsTextLikeContentType(string contentType)
    {
        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return true;
        return InlineTextContentTypes.Contains(contentType);
    }
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
