namespace Mailvec.Core.Attachments;

/// <summary>
/// Filename / content-type resolution shared by the parser (which names a part
/// it has just decoded) and Core (where <c>view_attachment</c> decides from the
/// stored row whether a part could be inlined at all, and only opens the
/// Maildir when the answer might be yes). Both paths MUST produce the same name
/// and type or the short-circuit changes behaviour instead of skipping work —
/// so both route through here rather than through two lookalike
/// implementations. Pure string functions; no MIME library involved.
/// </summary>
public static class AttachmentNaming
{
    private static readonly HashSet<string> InlineTextContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json", "application/xml", "application/yaml", "application/x-yaml",
        "application/javascript", "application/x-sh", "application/sql",
        "application/csv", "application/x-csv",
    };

    /// <summary>
    /// A safe filename: path separators stripped (a malicious / careless
    /// filename like "../../etc/passwd" could otherwise land outside a download
    /// directory), with a synthesized fallback when the part has no
    /// Content-Disposition filename / Content-Type name.
    /// </summary>
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
    /// The most specific content type we can. Many mail clients attach PDFs /
    /// docs / images as <c>application/octet-stream</c> and rely on the
    /// filename extension for type info; substitute when we recognise the
    /// extension. Load-bearing for the inline short-circuit: reading the raw
    /// column would classify a JPEG as un-inlineable binary.
    /// </summary>
    public static string ResolveContentType(string? declaredContentType, string fileName)
    {
        var declared = declaredContentType;
        if (string.IsNullOrEmpty(declared)) declared = "application/octet-stream";

        var isGeneric = string.Equals(declared, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(declared, "binary/octet-stream", StringComparison.OrdinalIgnoreCase);
        if (!isGeneric) return declared;

        return MimeForExtension(Path.GetExtension(fileName).ToLowerInvariant()) ?? declared;
    }

    /// <summary>
    /// Known MIME for a lowercase filename extension including the leading dot
    /// ('.pdf'), or null. Also consumed by SearchFilterSql's attachmentType
    /// filter so "pdf" matches correctly-typed attachments with odd filenames.
    /// </summary>
    public static string? MimeForExtension(string ext) => ext switch
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

    /// <summary>Whether an inline-text decode would be attempted for this type at all.</summary>
    public static bool IsTextLikeContentType(string contentType)
    {
        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return true;
        return InlineTextContentTypes.Contains(contentType);
    }

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
}
