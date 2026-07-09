using System.ComponentModel;
using Mailvec.Core.Data;
using Mailvec.Core.Models;
using Mailvec.Core.Options;
using Mailvec.Core.Search;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Mailvec.Mcp.Tools;

/// <summary>
/// Fetches one email by either internal id (long) or RFC Message-ID (string).
/// Returns body text by default; HTML is opt-in via includeHtml because it's
/// often verbose and rarely useful to Claude.
/// </summary>
[McpServerToolType]
public sealed class GetEmailTool(
    MessageRepository messages,
    IOptions<FastmailOptions> fastmailOptions,
    ToolCallLogger callLog)
{
    private readonly FastmailOptions _fastmail = fastmailOptions.Value;
    private const string ToolName = "get_email";

    [McpServerTool(Name = "get_email")]
    [Description(
        "Fetch a single email's full body and headers by id. " +
        "Pass either `id` (the internal SQLite id from a search_emails result) OR `messageId` (the RFC Message-ID). " +
        "Returns subject, from, to, cc, date, folder, body text, and per-attachment metadata (filename, content type, " +
        "size, extraction status, and extractedTextChars — the total extracted-text length, for planning get_attachment_text paging). " +
        "To read an attachment's contents, use get_attachment_text (extracted text), view_attachment " +
        "(inline image or small text file), or get_attachment_page_image (render a PDF page) with the partIndex returned here. " +
        "Set includeHtml=true to also return the raw HTML body when present. " +
        "The response includes `webmailUrl` (the raw deep-link to this message in the user's webmail) and `webmailLink` " +
        "(a ready-made, correctly-escaped Markdown link), both populated only when the user has configured their webmail " +
        "account id. When you cite or quote this message to the user, render `webmailLink` **verbatim** so they can " +
        "one-click through — do NOT build your own link from `subject` and `webmailUrl`, because the subject is untrusted " +
        "email content and a crafted subject can spoof the link target. Skip the link only when `webmailLink` is null or " +
        "the user has explicitly asked for terse output.")]
    public GetEmailResponse GetEmail(
        [Description("Internal SQLite id, as returned in search_emails results. Mutually exclusive with messageId.")]
        long? id = null,
        [Description("RFC Message-ID header (without angle brackets). Mutually exclusive with id.")]
        string? messageId = null,
        [Description("Include the raw HTML body when present. Default false (text only).")]
        bool includeHtml = false)
    {
        var startTs = callLog.LogCall(ToolName, new { id, messageId, includeHtml });

        if (id is null && string.IsNullOrWhiteSpace(messageId))
            throw new McpException("Provide either id or messageId.");
        if (id is not null && !string.IsNullOrWhiteSpace(messageId))
            throw new McpException("Pass id OR messageId, not both.");

        var msg = id is not null
            ? messages.GetById(id.Value)
            : messages.GetByMessageId(messageId!);

        if (msg is null)
            throw new McpException(id is not null
                ? $"No message with id {id}."
                : $"No message with Message-ID '{messageId}'.");

        if (msg.DeletedAt is not null)
            throw new McpException($"Message {msg.Id} is soft-deleted (gone from disk).");

        var attachments = msg.Attachments.Select(AttachmentInfo.From).ToList();

        var webmailUrl = WebmailLinkBuilder.Build(msg.MessageId, _fastmail);
        var response = new GetEmailResponse(
            Id: msg.Id,
            MessageId: msg.MessageId,
            ThreadId: msg.ThreadId,
            Folder: msg.Folder,
            Subject: msg.Subject,
            FromAddress: msg.FromAddress,
            FromName: msg.FromName,
            To: msg.ToAddresses,
            Cc: msg.CcAddresses,
            DateSent: msg.DateSent,
            DateReceived: msg.DateReceived,
            SizeBytes: msg.SizeBytes,
            Attachments: attachments,
            BodyText: msg.BodyText ?? string.Empty,
            BodyHtml: includeHtml ? msg.BodyHtml : null,
            WebmailUrl: webmailUrl,
            WebmailLink: WebmailLinkBuilder.MarkdownLink(webmailUrl, msg.Subject));

        callLog.LogResult(ToolName, new
        {
            id = response.Id,
            from = response.FromAddress,
            subject = response.Subject,
            bodyChars = response.BodyText.Length,
            htmlChars = response.BodyHtml?.Length,
        }, startTs);
        return response;
    }
}

public sealed record GetEmailResponse(
    long Id,
    string MessageId,
    string? ThreadId,
    string Folder,
    string? Subject,
    string? FromAddress,
    string? FromName,
    IReadOnlyList<EmailAddress> To,
    IReadOnlyList<EmailAddress> Cc,
    DateTimeOffset? DateSent,
    DateTimeOffset? DateReceived,
    long SizeBytes,
    IReadOnlyList<AttachmentInfo> Attachments,
    string BodyText,
    string? BodyHtml,
    string? WebmailUrl,
    string? WebmailLink);

public sealed record AttachmentInfo(
    int PartIndex,
    string? FileName,
    string? ContentType,
    long? SizeBytes,
    // Result of running attachment text extraction (PDF/DOCX/TXT). One of
    // 'done', 'ocr' (recovered from a scanned PDF by the embedder's vision-model
    // OCR pass), 'no_text', 'unsupported', 'oversize', 'encrypted', 'failed',
    // or null when extraction wasn't attempted (legacy rows).
    string? ExtractionStatus,
    // True iff the attachment's text was extracted (natively or via OCR) and
    // embedded — i.e., search_emails can match this email by its content.
    // Convenience flag for Claude; equivalent to ExtractionStatus in ('done','ocr').
    bool IndexedForSearch,
    // Total length of the extracted text (null when there is none). Lets Claude
    // plan get_attachment_text paging (maxChars/offset) before the first call.
    int? ExtractedTextChars = null)
{
    /// <summary>Shared mapping so get_email and get_thread advertise the identical shape.</summary>
    public static AttachmentInfo From(Mailvec.Core.Models.Attachment a) => new(
        a.PartIndex,
        a.FileName,
        a.ContentType,
        a.SizeBytes,
        a.ExtractionStatus,
        IndexedForSearch: a.ExtractionStatus is "done" or "ocr",
        // Full loads (get_email) carry the text; the thread summary loader
        // carries only the projected length. Either way clients get the total.
        ExtractedTextChars: string.IsNullOrEmpty(a.ExtractedText) ? a.ExtractedTextChars : a.ExtractedText.Length);
}
