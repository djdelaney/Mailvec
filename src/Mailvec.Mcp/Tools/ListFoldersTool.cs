using System.ComponentModel;
using Mailvec.Core.Data;
using ModelContextProtocol.Server;

namespace Mailvec.Mcp.Tools;

/// <summary>
/// One row per non-empty folder, sorted alphabetically. Lets Claude discover
/// the folder names available before calling search_emails with a folder
/// filter — without this, Claude has to guess names like "INBOX" or "Archive".
/// </summary>
[McpServerToolType]
public sealed class ListFoldersTool(MessageRepository messages)
{
    [McpServerTool(Name = "list_folders")]
    [Description(
        "List all Maildir folders that contain messages, with per-folder counts and date ranges. " +
        "Use this to discover the folder names before passing one to search_emails as a filter. " +
        "Soft-deleted messages are excluded from counts.")]
    public ListFoldersResponse ListFolders()
    {
        var stats = messages.FolderStats();
        return new ListFoldersResponse(stats.Count, stats);
    }
}

public sealed record ListFoldersResponse(
    int Count,
    IReadOnlyList<Mailvec.Core.Models.FolderStats> Folders);
