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
/// Reserved for the explicit, user-initiated download paths — the tray's Save
/// button (<c>/tray/attachment</c>) and <c>mailvec extract-attachments</c> — not
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
    /// Decode the attachment at <paramref name="partIndex"/> and write the
    /// bytes to <see cref="DownloadDir"/>. The output filename is
    /// `{messageId}-{partIndex}-{sanitized-filename}` so collisions across
    /// messages are impossible and the originating email is greppable.
    ///
    /// If the target file already exists with the same size we skip rewriting
    /// (idempotent re-fetches are cheap), but `wasReused` is set so callers
    /// can surface that fact.
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
    public InlineAttachment ExtractInMemory(Message message, int partIndex)
    {
        ArgumentNullException.ThrowIfNull(message);

        var data = _reader.Read(message, partIndex);
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
        var att = ExtractInMemory(message, partIndex);

        // Prefix with message id + part index — guarantees no collisions across
        // emails that happened to attach files with the same name, and keeps
        // the originating email greppable from the saved filename.
        var outputName = $"{message.Id}-{partIndex}-{att.FileName}";
        var targetPath = ResolveSafeOutputPath(_downloadDir, outputName);

        bool wasReused = TryReuseExisting(targetPath, att.SizeBytes);
        if (!wasReused)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            // Write to a sibling temp file then rename so a concurrent reader
            // never sees a partial file at targetPath.
            var tempPath = targetPath + ".part";
            try
            {
                File.WriteAllBytes(tempPath, att.Bytes);
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

    private static bool TryReuseExisting(string path, long expectedSize)
    {
        // If the file is already there with the right size, treat it as cached.
        // We don't hash because re-decoding from the Maildir is fast and would
        // be the dominant cost anyway. Size is a good-enough fingerprint.
        var info = new FileInfo(path);
        return info.Exists && info.Length == expectedSize;
    }

    /// <summary>
    /// Pulls a safe filename out of the MIME entity. Strips path separators
    /// (a malicious / careless filename like "../../etc/passwd" could otherwise
    /// land outside the download directory). Falls back to a synthesized name
    /// when the part has no Content-Disposition filename / Content-Type name.
    /// </summary>
    private static string ResolveSafeFileName(MimeEntity entity, int partIndex)
    {
        var raw = entity.ContentDisposition?.FileName ?? entity.ContentType?.Name;
        if (string.IsNullOrWhiteSpace(raw))
        {
            var ext = ExtensionFromContentType(entity.ContentType?.MimeType);
            return $"attachment-{partIndex}{ext}";
        }
        var safe = Path.GetFileName(raw).Replace('\0', '_').Trim();
        if (string.IsNullOrEmpty(safe))
        {
            var ext = ExtensionFromContentType(entity.ContentType?.MimeType);
            return $"attachment-{partIndex}{ext}";
        }
        return safe;
    }

    /// <summary>
    /// Resolve the most specific content type we can. Many mail clients attach
    /// PDFs / docs / images with `Content-Type: application/octet-stream` and
    /// rely on the filename extension for type info. The text response and
    /// image-detection branch both benefit from a real MIME, so substitute
    /// when we recognise the extension.
    /// </summary>
    private static string ResolveContentType(MimeEntity entity, string fileName)
    {
        var declared = entity.ContentType?.MimeType;
        if (string.IsNullOrEmpty(declared)) declared = "application/octet-stream";

        var isGeneric = string.Equals(declared, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(declared, "binary/octet-stream", StringComparison.OrdinalIgnoreCase);
        if (!isGeneric) return declared;

        var fromExt = MimeFromExtension(fileName);
        return fromExt ?? declared;
    }

    private static string? MimeFromExtension(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
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

    private static bool IsTextLikeContentType(string contentType)
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
