using System.ComponentModel;
using Mailvec.Core;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Mailvec.Mcp.Tools;

/// <summary>
/// One row per non-empty folder, sorted alphabetically. Lets Claude discover
/// the folder names available before calling search_emails with a folder
/// filter — without this, Claude has to guess names like "INBOX" or "Archive".
/// </summary>
[McpServerToolType]
public sealed class ListFoldersTool(MessageRepository messages, IOptions<ArchiveOptions> archiveOptions, ToolCallLogger callLog)
{
    private const string ToolName = "list_folders";

    [McpServerTool(Name = "list_folders")]
    [Description(
        "List all Maildir folders that contain messages, with per-folder counts and date ranges. " +
        "Use this to discover the folder names before passing one to search_emails as a filter. " +
        "Soft-deleted messages are excluded from counts.")]
    public ListFoldersResponse ListFolders()
    {
        var startTs = callLog.LogCall(ToolName, new { });
        var stats = messages.FolderStats();
        // An empty folder list is an empty archive — same "why" hint as
        // search_emails so the client LLM can explain instead of guessing.
        var setupHint = stats.Count > 0 ? null : SetupHints.EmptyArchiveHint(
            totalMessages: 0,
            SharedConfig.SharedConfigFileExists(),
            PathExpansion.Expand(archiveOptions.Value.DatabasePath));
        var response = new ListFoldersResponse(stats.Count, stats, setupHint);
        callLog.LogResult(ToolName, new { count = response.Count }, startTs);
        return response;
    }
}

public sealed record ListFoldersResponse(
    int Count,
    IReadOnlyList<Mailvec.Core.Models.FolderStats> Folders,
    // Additive: populated only when there are no folders at all. See SetupHints.
    string? SetupHint = null);
