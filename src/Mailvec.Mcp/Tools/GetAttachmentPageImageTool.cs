using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mailvec.Mcp.Tools;

/// <summary>
/// Renders a single page of a PDF attachment to a JPEG and returns it as an
/// <see cref="ImageContentBlock"/> — the one binary content type that renders
/// reliably on every Claude client, including a remote connector where
/// non-image blobs don't. This is the high-fidelity path for tables, charts,
/// signatures, and scanned/image-only PDFs that <c>get_attachment_text</c>
/// can't read (no embedded text). One page per call keeps the vision-token
/// cost bounded: read the text first to find the page, then render that page.
///
/// Reuses <see cref="AttachmentExtractor"/> to pull the PDF bytes out of the
/// Maildir (so the file also lands in the download dir, same as
/// get_attachment), then rasterises via <see cref="PdfRenderer"/> (PDFium).
/// Platform-annotated because PDFium is native — the server only runs on
/// macOS / Linux / Windows.
/// </summary>
[McpServerToolType]
public sealed class GetAttachmentPageImageTool(
    MessageRepository messages,
    AttachmentExtractor extractor,
    ToolCallLogger callLog)
{
    private const string ToolName = "get_attachment_page_image";

    [McpServerTool(Name = "get_attachment_page_image")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    [Description(
        "Render one page of a PDF attachment to a JPEG image (returned inline as an ImageContentBlock that Claude can " +
        "see). Identify the email with `id` OR `messageId`, the attachment with `partIndex` from get_email, and the page " +
        "with `page` (1-based, default 1). Use this when the layout matters and text extraction is lossy or empty: " +
        "tables, charts, forms, signatures, or scanned / image-only PDFs that get_attachment_text can't read. " +
        "Only PDFs are supported. One page per call — call get_attachment_text first to locate the page you need, then " +
        "render just that page to keep the response small.")]
    public CallToolResult GetAttachmentPageImage(
        [Description("0-based index from the Attachments list returned by get_email.")]
        int partIndex,
        [Description("1-based page number to render. Default 1.")]
        int page = 1,
        [Description("Internal SQLite id of the email. Mutually exclusive with messageId.")]
        long? id = null,
        [Description("RFC Message-ID header (without angle brackets). Mutually exclusive with id.")]
        string? messageId = null)
    {
        var startTs = callLog.LogCall(ToolName, new { id, messageId, partIndex, page });

        if (id is null && string.IsNullOrWhiteSpace(messageId))
            throw new McpException("Provide either id or messageId.");
        if (id is not null && !string.IsNullOrWhiteSpace(messageId))
            throw new McpException("Pass id OR messageId, not both.");
        if (page < 1)
            throw new McpException("page must be 1 or greater.");

        var msg = id is not null ? messages.GetById(id.Value) : messages.GetByMessageId(messageId!);
        if (msg is null)
            throw new McpException(id is not null
                ? $"No message with id {id}."
                : $"No message with Message-ID '{messageId}'.");

        if (msg.DeletedAt is not null)
            throw new McpException($"Message {msg.Id} is soft-deleted (gone from disk).");
        if (!msg.HasAttachments)
            throw new McpException($"Message {msg.Id} has no attachments.");

        ExtractResult result;
        try
        {
            result = extractor.Extract(msg, partIndex);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            throw new McpException(ex.Message);
        }

        if (!IsPdf(result.ContentType, result.FileName))
            throw new McpException(
                $"partIndex {partIndex} ('{result.FileName}', {result.ContentType}) is not a PDF. " +
                "This tool only renders PDFs — use get_attachment_text for a document's text or get_attachment for the raw file.");

        byte[] pdf;
        try
        {
            pdf = File.ReadAllBytes(result.FilePath);
        }
        catch (IOException ex)
        {
            throw new McpException($"Could not read the extracted PDF '{result.FileName}': {ex.Message}");
        }

        int pageCount;
        byte[] jpeg;
        try
        {
            pageCount = PdfRenderer.PageCount(pdf);
            if (page > pageCount)
                throw new McpException($"'{result.FileName}' has {pageCount} page(s); page {page} is out of range.");
            jpeg = PdfRenderer.RenderPageJpeg(pdf, page - 1);
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // PDFium throws on encrypted or corrupt PDFs.
            throw new McpException(
                $"Could not render '{result.FileName}' page {page}: {ex.Message}. " +
                "The PDF may be encrypted or corrupt; try get_attachment_text or get_attachment.");
        }

        var content = new List<ContentBlock>
        {
            new TextContentBlock
            {
                Text = $"Rendered page {page} of {pageCount} from {result.FileName} as a JPEG image.",
            },
            new ImageContentBlock
            {
                // Same encoding quirk as get_attachment: the SDK's Data setter
                // takes the UTF-8 bytes of the base64 string, not the raw bytes.
                Data = Encoding.UTF8.GetBytes(Convert.ToBase64String(jpeg)),
                MimeType = "image/jpeg",
            },
        };

        callLog.LogResult(ToolName, new { id = msg.Id, partIndex, page, pageCount, jpegBytes = jpeg.Length }, startTs);
        return new CallToolResult { Content = content };
    }

    private static bool IsPdf(string? contentType, string? fileName) =>
        string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
        || (fileName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ?? false);
}
