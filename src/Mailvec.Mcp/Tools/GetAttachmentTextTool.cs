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
/// <c>attachments.extracted_text</c>. Unlike <see cref="ViewAttachmentTool"/>
/// this touches neither the Maildir nor the download directory — it's a pure
/// DB read returning a <see cref="TextContentBlock"/>, which is the one
/// content type that renders reliably on every Claude client including a
/// remote (HTTP/OAuth) connector, where filesystem paths are meaningless and
/// non-image binary blocks don't render. This is the "what does this document
/// say" path; <c>view_attachment</c> shows images and small text files inline,
/// and <c>get_attachment_page_image</c> renders PDF pages.
///
/// When extraction didn't produce text (scanned/image-only PDF, encrypted,
/// oversize, unsupported type, or a failure) we say so explicitly, keyed off
/// the stored <c>extraction_status</c>, and point at whichever viewer tool
/// can still help.
/// </summary>
[McpServerToolType]
public sealed class GetAttachmentTextTool(
    MessageRepository messages,
    ToolCallLogger callLog)
{
    private const string ToolName = "get_attachment_text";

    // A single extracted document can be up to 2,000,000 chars (the extraction
    // output cap) — far past any client's context budget in one tool result.
    // Default to a window that comfortably covers typical documents; callers
    // page through bigger ones with `offset`.
    internal const int DefaultMaxChars = 50_000;
    internal const int MaxMaxChars = 200_000;

    [McpServerTool(Name = "get_attachment_text")]
    [Description(
        "Return the extracted plain text of a single attachment (PDF, DOCX, etc.) that Mailvec indexed at ingest time. " +
        "Identify the email with either `id` (the internal SQLite id) OR `messageId` (the RFC Message-ID), and the " +
        "attachment with `partIndex` from the get_email response (0-based, in MIME order). " +
        "Prefer this over view_attachment when you just need to READ or summarise a document's contents — it returns the " +
        "text directly with no filesystem dependency, so it works the same locally and over a remote connection. " +
        "Long documents are windowed: at most `maxChars` characters (default 50,000) starting at `offset` are returned, " +
        "with the total length and the next offset stated when truncated — page with `offset` until you have what you need " +
        "(get_email's per-attachment `extractedTextChars` tells you the total up front). " +
        "Text extraction loses layout, so tables and multi-column content may be flattened; if the attachment is a " +
        "scanned / image-only PDF (no embedded text), encrypted, too large, or an unsupported type, this tool says so " +
        "and suggests the fallback: get_attachment_page_image for PDF pages, view_attachment for images and small text files.")]
    public CallToolResult GetAttachmentText(
        [Description("0-based index from the Attachments list returned by get_email.")]
        int partIndex,
        [Description("Internal SQLite id of the email, as returned in search_emails / get_email results. Mutually exclusive with messageId.")]
        long? id = null,
        [Description("RFC Message-ID header (without angle brackets). Mutually exclusive with id.")]
        string? messageId = null,
        [Description("Maximum characters to return in this call. Default 50,000; capped at 200,000.")]
        int? maxChars = null,
        [Description("0-based character offset to start from, for paging through a long document. Default 0.")]
        int offset = 0)
    {
        var startTs = callLog.LogCall(ToolName, new { id, messageId, partIndex, maxChars, offset });

        if (id is null && string.IsNullOrWhiteSpace(messageId))
            throw new McpException("Provide either id or messageId.");
        if (id is not null && !string.IsNullOrWhiteSpace(messageId))
            throw new McpException("Pass id OR messageId, not both.");
        if (offset < 0)
            throw new McpException("offset must be 0 or greater.");
        if (maxChars is < 1)
            throw new McpException("maxChars must be 1 or greater.");

        var window = Math.Min(maxChars ?? DefaultMaxChars, MaxMaxChars);

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
            var text = attachment.ExtractedText;
            var how = attachment.ExtractionStatus == AttachmentTextExtractor.StatusOcr ? "OCR text" : "Extracted text";

            if (offset >= text.Length)
            {
                content.Add(new TextContentBlock
                {
                    Text = $"offset {offset:N0} is past the end of {name} — its extracted text is {text.Length:N0} chars total.",
                });
                callLog.LogResult(ToolName, new { id = msg.Id, partIndex, status = attachment.ExtractionStatus, chars = 0, offset }, startTs);
                return new CallToolResult { Content = content };
            }

            var (start, slice) = SliceWindow(text, offset, window);
            var end = start + slice.Length;
            var header = end < text.Length || start > 0
                ? $"{how} from {name} (partIndex {partIndex}): chars {start:N0}–{end:N0} of {text.Length:N0}" +
                  (end < text.Length ? $". Call again with offset={end} for the next chunk:" : " (final chunk):")
                : $"{how} from {name} (partIndex {partIndex}, {text.Length:N0} chars):";

            content.Add(new TextContentBlock { Text = header });
            content.Add(new TextContentBlock { Text = slice });
            callLog.LogResult(ToolName, new { id = msg.Id, partIndex, status = attachment.ExtractionStatus, chars = slice.Length, offset = start, total = text.Length }, startTs);
        }
        else
        {
            content.Add(new TextContentBlock { Text = UnavailableMessage(name, attachment.ExtractionStatus) });
            callLog.LogResult(ToolName, new { id = msg.Id, partIndex, status = attachment.ExtractionStatus, chars = 0 }, startTs);
        }

        return new CallToolResult { Content = content };
    }

    /// <summary>
    /// Slice [offset, offset+window) out of <paramref name="text"/>, nudging
    /// both ends off the middle of a surrogate pair so the window is always
    /// valid UTF-16 (a split pair would serialize as U+FFFD). Internal so
    /// view_attachment's inline-text truncation shares the same slicer.
    /// </summary>
    internal static (int Start, string Slice) SliceWindow(string text, int offset, int window)
    {
        var start = offset;
        if (start > 0 && char.IsLowSurrogate(text[start]))
            start--;
        var end = Math.Min(start + window, text.Length);
        if (end < text.Length && char.IsHighSurrogate(text[end - 1]))
        {
            // Shrink to exclude the split pair — unless that would empty the
            // window, in which case grow to include it (its low half is at
            // `end`, so end+1 is in range).
            end = end - start > 1 ? end - 1 : end + 1;
        }
        return (start, text[start..end]);
    }

    private static string UnavailableMessage(string name, string? status) => status switch
    {
        AttachmentTextExtractor.StatusNoText =>
            $"No text could be extracted from '{name}' — it has no embedded text layer (e.g. a scanned or image-only PDF). " +
            "Use get_attachment_page_image to view its pages.",
        AttachmentTextExtractor.StatusEncrypted =>
            $"'{name}' is encrypted, so its text could not be extracted and its pages cannot be rendered. " +
            "Mailvec cannot decrypt it — the user can save the file via the tray's Save button or `mailvec extract-attachments` and open it themselves.",
        AttachmentTextExtractor.StatusOversize =>
            $"'{name}' exceeds the text-extraction size cap, so its text was not extracted. " +
            "If it is a PDF, get_attachment_page_image can still render individual pages.",
        AttachmentTextExtractor.StatusUnsupported =>
            $"'{name}' is a type Mailvec does not extract text from. " +
            "If it is an image or a small text file, view_attachment can show it inline.",
        AttachmentTextExtractor.StatusFailed =>
            $"Text extraction failed for '{name}'. " +
            "Try get_attachment_page_image for a PDF, or view_attachment for an image or small text file.",
        AttachmentTextExtractor.StatusOcr =>
            $"'{name}' was OCR-processed but no text was recovered (likely a blank scan). " +
            "Use get_attachment_page_image to view the pages.",
        null =>
            $"'{name}' has no extraction record yet (it predates attachment text extraction, or the embedder hasn't " +
            "processed it). Try get_attachment_page_image for a PDF, or view_attachment for an image or small text file.",
        _ =>
            $"No extracted text is available for '{name}' (status: {status}). " +
            "Try get_attachment_page_image for a PDF, or view_attachment for an image or small text file.",
    };
}
