using System.ComponentModel;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Search;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Mailvec.Mcp.Tools;

/// <summary>
/// Fetches every message in the same thread as the supplied id/Message-ID,
/// sorted oldest-first. Default returns headers + a short body snippet only;
/// includeBodies=true expands to full body text — use sparingly because long
/// threads add up fast.
/// </summary>
[McpServerToolType]
public sealed class GetThreadTool(MessageRepository messages, IOptions<FastmailOptions> fastmailOptions)
{
    private readonly FastmailOptions _fastmail = fastmailOptions.Value;
    [McpServerTool(Name = "get_thread")]
    [Description(
        "Fetch all messages in a thread (chronological, oldest first). " +
        "Pass either `id` or `messageId` for any message that's part of the thread; the tool will resolve the thread via thread_id. " +
        "Default returns subject/from/date/snippet for each message — set includeBodies=true to include full body text " +
        "(token-heavy on long threads, so prefer the default and follow up with get_email on specific messages).")]
    public GetThreadResponse GetThread(
        [Description("Internal SQLite id of any message in the thread. Mutually exclusive with messageId.")]
        long? id = null,
        [Description("RFC Message-ID of any message in the thread. Mutually exclusive with id.")]
        string? messageId = null,
        [Description("Include full body text for every message in the thread. Default false (snippet only).")]
        bool includeBodies = false)
    {
        if (id is null && string.IsNullOrWhiteSpace(messageId))
            throw new McpException("Provide either id or messageId.");
        if (id is not null && !string.IsNullOrWhiteSpace(messageId))
            throw new McpException("Pass id OR messageId, not both.");

        var thread = messages.GetThreadByMessageId(id, messageId);
        if (thread.Count == 0)
            throw new McpException(id is not null
                ? $"No message with id {id} (or its thread is empty after soft-deletes)."
                : $"No message with Message-ID '{messageId}' (or its thread is empty after soft-deletes).");

        var rootThreadId = thread[0].ThreadId;
        var entries = thread.Select(m => new ThreadEntry(
            Id: m.Id,
            MessageId: m.MessageId,
            Folder: m.Folder,
            Subject: m.Subject,
            FromAddress: m.FromAddress,
            FromName: m.FromName,
            DateSent: m.DateSent,
            Snippet: BuildSnippet(m.BodyText),
            BodyText: includeBodies ? (m.BodyText ?? string.Empty) : null,
            WebmailUrl: WebmailLinkBuilder.Build(m.MessageId, _fastmail)
        )).ToList();

        return new GetThreadResponse(
            ThreadId: rootThreadId,
            Count: entries.Count,
            Messages: entries);
    }

    private static string BuildSnippet(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        var collapsed = System.Text.RegularExpressions.Regex.Replace(body.Trim(), @"\s+", " ");
        return collapsed.Length <= 200 ? collapsed : collapsed[..200] + "…";
    }
}

public sealed record GetThreadResponse(
    string? ThreadId,
    int Count,
    IReadOnlyList<ThreadEntry> Messages);

public sealed record ThreadEntry(
    long Id,
    string MessageId,
    string Folder,
    string? Subject,
    string? FromAddress,
    string? FromName,
    DateTimeOffset? DateSent,
    string Snippet,
    string? BodyText,
    string? WebmailUrl);
