using System.ComponentModel;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using Mailvec.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mailvec.Mcp.Tools;

/// <summary>
/// Returns the plain text Mailvec already extracted from an attachment
/// (PDF via PdfPig, DOCX via OpenXml) at index time, straight from
/// <c>attachments.extracted_text</c>. Unlike <see cref="GetAttachmentTool"/>
/// this touches neither the Maildir nor the download directory — it's a pure
/// DB read returning a <see cref="TextContentBlock"/>, which is the one
/// content type that renders reliably on every Claude client including a
/// remote (HTTP/OAuth) connector, where filesystem paths are meaningless and
/// non-image binary blocks don't render. This is the "what does this document
/// say" path; use <c>get_attachment</c> when the user needs the actual file.
///
/// When extraction didn't produce text (scanned/image-only PDF, encrypted,
/// oversize, unsupported type, or a failure) we say so explicitly, keyed off
/// the stored <c>extraction_status</c>, and point at <c>get_attachment</c>.
/// </summary>
[McpServerToolType]
public sealed class GetAttachmentTextTool(
    MessageRepository messages,
    ToolCallLogger callLog)
{
    private const string ToolName = "get_attachment_text";

    [McpServerTool(Name = "get_attachment_text")]
    [Description(
        "Return the extracted plain text of a single attachment (PDF, DOCX, etc.) that Mailvec indexed at ingest time. " +
        "Identify the email with either `id` (the internal SQLite id) OR `messageId` (the RFC Message-ID), and the " +
        "attachment with `partIndex` from the get_email response (0-based, in MIME order). " +
        "Prefer this over get_attachment when you just need to READ or summarise a document's contents — it returns the " +
        "text directly with no filesystem dependency, so it works the same locally and over a remote connection. " +
        "Text extraction loses layout, so tables and multi-column content may be flattened; if the attachment is a " +
        "scanned / image-only PDF (no embedded text), encrypted, too large, or an unsupported type, this tool says so " +
        "and you should fall back to get_attachment to fetch the file itself.")]
    public CallToolResult GetAttachmentText(
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

        var attachment = msg.Attachments.FirstOrDefault(a => a.PartIndex == partIndex);
        if (attachment is null)
            throw new McpException(
                $"Message {msg.Id} has no attachment at partIndex {partIndex}. Call get_email to list attachment part indexes.");

        var content = new List<ContentBlock>();
        var name = attachment.FileName ?? "attachment";

        // 'ocr' is searchable text like 'done' — it's a scanned document the
        // embedder recovered via vision-model OCR. Refusing it here would
        // contradict search (which matches on that text) and get_email
        // (which reports it IndexedForSearch).
        if (attachment.ExtractionStatus is AttachmentTextExtractor.StatusDone or AttachmentTextExtractor.StatusOcr
            && !string.IsNullOrEmpty(attachment.ExtractedText))
        {
            var how = attachment.ExtractionStatus == AttachmentTextExtractor.StatusOcr ? "OCR text" : "Extracted text";
            content.Add(new TextContentBlock
            {
                Text = $"{how} from {name} (partIndex {partIndex}, {attachment.ExtractedText.Length:N0} chars):",
            });
            content.Add(new TextContentBlock { Text = attachment.ExtractedText });
            callLog.LogResult(ToolName, new { id = msg.Id, partIndex, status = attachment.ExtractionStatus, chars = attachment.ExtractedText.Length }, startTs);
        }
        else
        {
            content.Add(new TextContentBlock { Text = UnavailableMessage(name, attachment.ExtractionStatus) });
            callLog.LogResult(ToolName, new { id = msg.Id, partIndex, status = attachment.ExtractionStatus, chars = 0 }, startTs);
        }

        return new CallToolResult { Content = content };
    }

    private static string UnavailableMessage(string name, string? status) => status switch
    {
        AttachmentTextExtractor.StatusNoText =>
            $"No text could be extracted from '{name}' — it has no embedded text layer (e.g. a scanned or image-only PDF). " +
            "Use get_attachment to fetch the file itself.",
        AttachmentTextExtractor.StatusEncrypted =>
            $"'{name}' is encrypted, so its text could not be extracted. Use get_attachment to fetch the file.",
        AttachmentTextExtractor.StatusOversize =>
            $"'{name}' exceeds the text-extraction size cap, so its text was not extracted. Use get_attachment to fetch the file.",
        AttachmentTextExtractor.StatusUnsupported =>
            $"'{name}' is a type Mailvec does not extract text from. Use get_attachment to fetch the file.",
        AttachmentTextExtractor.StatusFailed =>
            $"Text extraction failed for '{name}'. Use get_attachment to fetch the file.",
        AttachmentTextExtractor.StatusOcr =>
            $"'{name}' was OCR-processed but no text was recovered (likely a blank scan). " +
            "Use get_attachment_page_image to view the pages.",
        null =>
            $"'{name}' has no extraction record yet (it predates attachment text extraction, or the embedder hasn't " +
            "processed it). Use get_attachment to fetch the file.",
        _ =>
            $"No extracted text is available for '{name}' (status: {status}). Use get_attachment to fetch the file.",
    };
}
