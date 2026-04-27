using Mailvec.Core;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Mailvec.Core.Parsing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailvec.Indexer.Services;

public sealed class MaildirScanner(
    IOptions<IngestOptions> ingestOptions,
    MessageParser parser,
    MessageRepository messages,
    SyncStateRepository syncState,
    ILogger<MaildirScanner> logger)
{
    private readonly string _maildirRoot = PathExpansion.Expand(ingestOptions.Value.MaildirRoot);

    public sealed record ScanResult(int Seen, int Upserted, int FailedToParse, int SoftDeleted);

    /// <summary>
    /// Walks every Maildir subfolder under MaildirRoot, parses messages, and
    /// reconciles deletions: any sync_state row not refreshed during this scan
    /// is treated as a removed message.
    /// </summary>
    public ScanResult ScanAll(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_maildirRoot))
        {
            logger.LogWarning("Maildir root {Path} does not exist; nothing to scan.", _maildirRoot);
            return new ScanResult(0, 0, 0, 0);
        }

        var scanStart = DateTimeOffset.UtcNow;
        var seen = 0;
        var upserted = 0;
        var failed = 0;

        foreach (var folderDir in EnumerateMaildirFolders(_maildirRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var folderName = MaildirPaths.FolderNameFor(_maildirRoot, folderDir);
            foreach (var subdir in new[] { "new", "cur" })
            {
                var sub = Path.Combine(folderDir, subdir);
                if (!Directory.Exists(sub)) continue;

                foreach (var file in Directory.EnumerateFiles(sub))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    seen++;

                    if (TryIngest(file, folderName, scanStart))
                    {
                        upserted++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }
        }

        var stale = syncState.StaleEntries(olderThan: scanStart);
        // A message-id with a fresh sync_state row (this scan's pass) was just
        // re-seen at a new path — treat it as a rename, not a deletion.
        var freshMessageIds = syncState.FreshMessageIds(since: scanStart);
        var staleByMessageId = stale
            .Where(e => e.MessageId is not null && !freshMessageIds.Contains(e.MessageId))
            .Select(e => e.MessageId!)
            .Distinct()
            .ToList();

        var softDeleted = 0;
        if (staleByMessageId.Count > 0)
        {
            var idsToMark = new List<long>(staleByMessageId.Count);
            foreach (var mid in staleByMessageId)
            {
                var msg = messages.GetByMessageId(mid);
                if (msg is { DeletedAt: null }) idsToMark.Add(msg.Id);
            }
            if (idsToMark.Count > 0)
            {
                softDeleted = messages.MarkDeleted(idsToMark, scanStart);
            }
        }

        if (stale.Count > 0)
        {
            syncState.Remove(stale.Select(e => e.MaildirFullPath));
        }

        logger.LogInformation(
            "MaildirScanner: seen={Seen} upserted={Upserted} parseFailed={Failed} softDeleted={SoftDeleted}",
            seen, upserted, failed, softDeleted);

        return new ScanResult(seen, upserted, failed, softDeleted);
    }

    private bool TryIngest(string filePath, string folderName, DateTimeOffset indexedAt)
    {
        try
        {
            var parsed = parser.ParseFile(filePath);
            var relPath = MaildirPaths.RelativeFolderPath(_maildirRoot, filePath);
            var fileName = Path.GetFileName(filePath);

            messages.Upsert(parsed, folderName, relPath, fileName, indexedAt);
            syncState.Upsert(filePath, parsed.MessageId, indexedAt);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse {Path}", filePath);
            // Refresh sync_state with no message_id so we don't treat the file as deleted next pass.
            try { syncState.Upsert(filePath, messageId: null, indexedAt); } catch { /* ignore */ }
            return false;
        }
    }

    /// <summary>
    /// A Maildir folder is any directory that itself contains the canonical
    /// new/ and cur/ subdirectories. Walks recursively so nested folders
    /// (e.g. Archive.2024) are picked up.
    /// </summary>
    private static IEnumerable<string> EnumerateMaildirFolders(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            if (Directory.Exists(Path.Combine(dir, "cur")) || Directory.Exists(Path.Combine(dir, "new")))
            {
                yield return dir;
            }

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var leaf = Path.GetFileName(sub);
                // Skip the maildir-internal subdirs themselves so they aren't reported as folders.
                if (leaf is "new" or "cur" or "tmp") continue;
                stack.Push(sub);
            }
        }
    }
}

internal static class MaildirPaths
{
    public static string FolderNameFor(string root, string folderDir)
    {
        var rel = Path.GetRelativePath(root, folderDir);
        if (rel == ".") return "INBOX";
        // mbsync's "Subfolders Verbatim" uses dot-separated names like "Archive.2024".
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// Relative directory path including the new/cur leaf, e.g. "INBOX/cur" or "Archive.2024/new".
    /// </summary>
    public static string RelativeFolderPath(string root, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath)!;
        var rel = Path.GetRelativePath(root, dir);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }
}
