using System.ComponentModel;
using Mailvec.Core.Attachments;
using Mailvec.Core.Data;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Mailvec.Mcp.Tools;

/// <summary>
/// Returns a message's raw header block, read from the Maildir <c>.eml</c> on
/// demand — for deliverability work (SPF/DKIM/DMARC/ARC verdicts, the
/// <c>Received</c> path, the provider's spam scoring), where the user would
/// otherwise copy "show original" out of webmail.
///
/// Not from the database: <c>messages.raw_headers</c> never held headers (the
/// indexer stored <c>HeaderList.ToString()</c>, the type name) and nothing
/// stores them now. The file is the only faithful copy, and reading it keeps
/// the order and folding that a parsed header list would lose.
/// </summary>
[McpServerToolType]
public sealed class GetEmailHeadersTool(
    MessageRepository messages,
    MaildirAttachmentReader reader,
    ToolCallLogger callLog)
{
    private const string ToolName = "get_email_headers";

    // Ceiling on the bytes read looking for the end of the header block. Real
    // blocks are a few KB to a few tens of KB (long Received/ARC chains); the
    // length is sender-chosen, so it is bounded regardless. Protocol size, not
    // a preference — same reasoning as view_attachment's pass-through cap.
    internal const int HeaderBlockMaxBytes = 256 * 1024;

    [McpServerTool(Name = "get_email_headers", ReadOnly = true, OpenWorld = false)]
    [Description(
        "Fetch an email's raw header block exactly as delivered — the same text as webmail's \"show original\" " +
        "header section, in original order with folding intact. Use it for deliverability and spam questions: " +
        "Authentication-Results (SPF/DKIM/DMARC verdicts), ARC-*, DKIM-Signature, Received-SPF, the Received chain, " +
        "Return-Path, and the receiving provider's spam headers (e.g. X-Spam-*, X-ME-*). " +
        "Pass either `id` (from search_emails / get_email) OR `messageId` (the RFC Message-ID). " +
        "Pass `names` to return only some fields (case-insensitive; a trailing * matches a prefix, e.g. " +
        "[\"Authentication-Results\", \"ARC-*\", \"Received\"]). `fieldCount` is the number returned, " +
        "`totalFieldCount` the number in the block, and `truncated` is true when the block exceeded 256 KB and was cut.\n\n" +
        "Headers are listed newest hop first. Only the fields the user's own mail provider added on receipt — the " +
        "topmost Received lines and the topmost Authentication-Results, whose first token (the authserv-id) names that " +
        "provider, e.g. *.messagingengine.com for Fastmail or mx.google.com for Gmail — record checks the provider " +
        "actually performed. Everything below them, including any Authentication-Results, ARC or Received header that " +
        "claims a pass, was written by the sender or an intermediary and can be forged.\n\n" +
        ToolText.UntrustedContent)]
    public GetEmailHeadersResponse GetEmailHeaders(
        [Description("Internal SQLite id, as returned in search_emails results. Mutually exclusive with messageId.")]
        long? id = null,
        [Description("RFC Message-ID header (without angle brackets). Mutually exclusive with id.")]
        string? messageId = null,
        [Description("Header field names to return, case-insensitive; end one with * to match a prefix (\"ARC-*\"). Omit for the whole block.")]
        string[]? names = null)
    {
        var startTs = callLog.LogCall(ToolName, new { id, messageId, names });

        if (id is null && string.IsNullOrWhiteSpace(messageId))
            throw new McpException("Provide either id or messageId.");
        if (id is not null && !string.IsNullOrWhiteSpace(messageId))
            throw new McpException("Pass id OR messageId, not both.");

        var msg = id is not null
            ? messages.GetById(id.Value, includeAttachmentText: false)
            : messages.GetByMessageId(messageId!, includeAttachmentText: false);

        if (msg is null)
            throw new McpException(id is not null
                ? $"No message with id {id}."
                : $"No message with Message-ID '{messageId}'.");

        if (msg.DeletedAt is not null)
            throw new McpException($"Message {msg.Id} is soft-deleted (gone from disk).");

        RawHeaderBlock block;
        try
        {
            block = reader.ReadHeaderBlock(msg, HeaderBlockMaxBytes);
        }
        catch (FileNotFoundException ex)
        {
            // The exception message is written to be client-safe (no path);
            // see MaildirAttachmentReader.ResolveExistingSource.
            throw new McpException(ex.Message);
        }

        var fields = block.Fields();
        var filter = names?.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
        string text;
        int returned;
        if (filter is { Length: > 0 })
        {
            var selected = fields.Where(f => RawHeaderBlock.Matches(f.Name, filter)).ToList();
            text = string.Concat(selected.Select(f => f.Raw));
            returned = selected.Count;
        }
        else
        {
            text = block.Text;
            returned = fields.Count;
        }

        var response = new GetEmailHeadersResponse(
            Id: msg.Id,
            MessageId: msg.MessageId,
            Folder: msg.Folder,
            RawHeaders: text,
            FieldCount: returned,
            TotalFieldCount: fields.Count,
            Truncated: block.Truncated);

        callLog.LogResult(ToolName, new
        {
            id = response.Id,
            fieldCount = response.FieldCount,
            totalFieldCount = response.TotalFieldCount,
            chars = text.Length,
            truncated = response.Truncated,
        }, startTs);
        return response;
    }
}

public sealed record GetEmailHeadersResponse(
    long Id,
    string MessageId,
    string Folder,
    /// <summary>The selected header fields, verbatim and in original order.</summary>
    string RawHeaders,
    /// <summary>Fields in <see cref="RawHeaders"/>.</summary>
    int FieldCount,
    /// <summary>Fields in the whole header block, before any <c>names</c> filter.</summary>
    int TotalFieldCount,
    /// <summary>True when the block exceeded the read ceiling and was cut at the last whole line.</summary>
    bool Truncated);
