using System.ComponentModel;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mailvec.Mcp.Tools;

/// <summary>
/// Returns an attachment's content inline, decoded entirely in memory — no bytes
/// are written to disk. Image attachments come back as an ImageContentBlock
/// (visible to Claude vision) and small text-ish files as a decoded text block,
/// so "describe this photo" / "what's in this CSV" work in one round trip.
///
/// Binary types we can't render inline (PDF, DOCX, zip, …) return only a summary
/// pointing at the right tool: get_attachment_text for extracted document text,
/// or get_attachment_page_image to view a PDF page. We deliberately do NOT ship
/// arbitrary binary back through MCP — Claude.ai's bridge maps every
/// EmbeddedResourceBlock to an image block regardless of MIME and rejects
/// non-image MIMEs as "unsupported image format" — and we no longer persist the
/// file to a download directory (that made every read leak mail content to disk
/// and is meaningless in a containerised deployment). The tray's Save button and
/// `mailvec extract-attachments` remain the explicit save-to-disk paths.
/// </summary>
[McpServerToolType]
public sealed class ViewAttachmentTool(
    MessageRepository messages,
    AttachmentExtractor extractor,
    ToolCallLogger callLog)
{
    private const string ToolName = "view_attachment";

    [McpServerTool(Name = "view_attachment")]
    [Description(
        "Show a single email attachment's content inline (nothing is written to disk). " +
        "Identify the email with either `id` (the internal SQLite id) OR `messageId` (the RFC Message-ID). " +
        "Identify the attachment with `partIndex` from the get_email response (0-based, in MIME order). " +
        "Image attachments are returned as an MCP ImageContentBlock (visible to Claude vision); small text-ish " +
        "files (text/*, application/json, etc., under ~256 KB) have their decoded UTF-8 text included as a text block. " +
        "For other binary types (PDF, DOCX, zip, …) the response is a short summary — use get_attachment_text to read " +
        "a document's extracted text, or get_attachment_page_image to view a PDF page as an image.")]
    public CallToolResult ViewAttachment(
        [Description("0-based index from the Attachments list returned by get_email.")]
        int partIndex,
        [Description("Internal SQLite id of the email, as returned in search_emails / get_email results. Mutually exclusive with messageId.")]
        long? id = null,
        [Description("RFC Message-ID header (without angle brackets). Mutually exclusive with id.")]
        string? messageId = null)
    {
        var startTs = callLog.LogCall(ToolName, new { id, messageId, partIndex });

        if (id is null && string.IsNullOrWhiteSpace(messageId))
            throw new McpException("Provide either id or messageId.");
        if (id is not null && !string.IsNullOrWhiteSpace(messageId))
            throw new McpException("Pass id OR messageId, not both.");

        var msg = id is not null ? messages.GetById(id.Value) : messages.GetByMessageId(messageId!);
        if (msg is null)
            throw new McpException(id is not null
                ? $"No message with id {id}."
                : $"No message with Message-ID '{messageId}'.");

        if (msg.DeletedAt is not null)
            throw new McpException($"Message {msg.Id} is soft-deleted (gone from disk).");

        if (!msg.HasAttachments)
            throw new McpException($"Message {msg.Id} has no attachments.");

        InlineAttachment att;
        try
        {
            att = extractor.ExtractInMemory(msg, partIndex);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }

        var isImage = IsImageContentType(att.ContentType);
        var content = new List<ContentBlock>
        {
            new TextContentBlock { Text = BuildSummary(att, imageInlined: isImage, textInlined: att.InlineText is not null) },
        };

        // Inline the decoded text for small text-ish files so Claude can read
        // CSV / JSON / logs in one round trip.
        if (att.InlineText is not null)
        {
            content.Add(new TextContentBlock { Text = att.InlineText });
        }

        // Inline images as ImageContentBlock so Claude vision works immediately.
        // This is the only binary path reliable across all current Claude clients
        // — non-image binary goes through a bridge that rejects everything as an
        // unsupported image, which is why other types get only the summary above.
        if (isImage)
        {
            // The SDK's Data setter takes the UTF-8 bytes of the base64 string,
            // not the raw bytes (counterintuitive, but per the SDK doc).
            var base64Utf8 = System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(att.Bytes));
            content.Add(new ImageContentBlock
            {
                Data = base64Utf8,
                MimeType = att.ContentType,
            });
        }

        callLog.LogResult(ToolName, new
        {
            fileName = att.FileName,
            contentType = att.ContentType,
            sizeBytes = att.SizeBytes,
            imageInlined = isImage,
            inlineChars = att.InlineText?.Length,
        }, startTs);

        return new CallToolResult { Content = content };
    }

    private static string BuildSummary(InlineAttachment att, bool imageInlined, bool textInlined)
    {
        var header = $"'{att.FileName}' ({att.ContentType}, {FormatSize(att.SizeBytes)})";
        if (imageInlined)
            return $"{header} — shown inline below.";
        if (textInlined)
            return $"{header} — decoded text included below.";
        return
            $"{header}. This type can't be shown inline. " +
            "For a PDF, call get_attachment_page_image to view a page as an image; " +
            "for any document, call get_attachment_text to read its extracted text.";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:N1} KB";
        return $"{kb / 1024:N1} MB";
    }

    private static bool IsImageContentType(string contentType) =>
        contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
