using System.ComponentModel;
using Mailvec.Core.Data;
using Mailvec.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Mailvec.Mcp.Tools;

/// <summary>
/// Fetches one email by either internal id (long) or RFC Message-ID (string).
/// Returns body text by default; HTML is opt-in via includeHtml because it's
/// often verbose and rarely useful to Claude.
/// </summary>
[McpServerToolType]
public sealed class GetEmailTool(MessageRepository messages)
{
    [McpServerTool(Name = "get_email")]
    [Description(
        "Fetch a single email's full body and headers by id. " +
        "Pass either `id` (the internal SQLite id from a search_emails result) OR `messageId` (the RFC Message-ID). " +
        "Returns subject, from, to, cc, date, folder, attachment metadata, and body text. " +
        "Set includeHtml=true to also return the raw HTML body when present.")]
    public GetEmailResponse GetEmail(
        [Description("Internal SQLite id, as returned in search_emails results. Mutually exclusive with messageId.")]
        long? id = null,
        [Description("RFC Message-ID header (without angle brackets). Mutually exclusive with id.")]
        string? messageId = null,
        [Description("Include the raw HTML body when present. Default false (text only).")]
        bool includeHtml = false)
    {
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

        return new GetEmailResponse(
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
            HasAttachments: msg.HasAttachments,
            BodyText: msg.BodyText ?? string.Empty,
            BodyHtml: includeHtml ? msg.BodyHtml : null);
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
    bool HasAttachments,
    string BodyText,
    string? BodyHtml);
