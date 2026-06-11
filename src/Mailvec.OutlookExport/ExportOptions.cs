using System.Globalization;

namespace Mailvec.OutlookExport;

public sealed class ExportOptions
{
    /// <summary>Folders skipped by default: non-mail noise and mutable items
    /// with unstable or missing Message-IDs (Drafts, Outbox). Matched against
    /// any single path segment, case-insensitively, and pruned recursively.</summary>
    public static readonly IReadOnlyList<string> DefaultExcludes =
    [
        "Deleted Items", "Junk Email", "Junk E-mail", "Outbox", "Drafts",
        "Sync Issues", "RSS Feeds", "RSS Subscriptions", "Conversation History",
    ];

    public string OutDir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MailvecExport");

    public DateTimeOffset? Since { get; private set; }
    public bool AllStores { get; private set; }
    public bool DryRun { get; private set; }
    public long MaxAttachmentBytes { get; private set; } = 25L * 1024 * 1024;
    public List<string> IncludeFolders { get; } = [];
    public List<string> ExcludeFolders { get; } = [.. DefaultExcludes];

    public const string Usage = """
        Mailvec.OutlookExport — export classic (Win32) Outlook mail to .eml in a Maildir tree.

        Usage: Mailvec.OutlookExport [options]

          --out <dir>              Output root (default: %USERPROFILE%\MailvecExport)
          --since <yyyy-MM-dd>     Only export messages received on/after this date
          --include <folderPath>   Only export this folder (and subfolders); repeatable.
                                   Paths are store-relative, e.g. "Inbox" or "Inbox/Projects".
          --exclude <folderName>   Skip folders with this name anywhere; repeatable
                                   (adds to defaults: Deleted Items, Junk Email, Drafts,
                                   Outbox, Sync Issues, RSS, Conversation History)
          --all-stores             Export every store/mailbox, not just the default one
          --max-attachment-mb <n>  Per-attachment size cap (default 25)
          --dry-run                List folders and item counts without writing anything
          --help                   Show this help

        Requires classic Outlook to be installed; start it (logged in) before running.
        Re-runs are incremental: already-exported messages are skipped by file name.
        """;

    public static ExportOptions Parse(string[] args)
    {
        var opts = new ExportOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next(string flag) =>
                i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{flag} requires a value");

            switch (args[i])
            {
                case "--out":
                    opts.OutDir = Path.GetFullPath(Next("--out"));
                    break;
                case "--since":
                    var raw = Next("--since");
                    if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var since))
                        throw new ArgumentException($"--since: could not parse '{raw}' (expected yyyy-MM-dd)");
                    opts.Since = since;
                    break;
                case "--include":
                    opts.IncludeFolders.Add(Next("--include").Trim().Trim('/'));
                    break;
                case "--exclude":
                    opts.ExcludeFolders.Add(Next("--exclude").Trim());
                    break;
                case "--all-stores":
                    opts.AllStores = true;
                    break;
                case "--max-attachment-mb":
                    var mb = Next("--max-attachment-mb");
                    if (!long.TryParse(mb, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                        throw new ArgumentException($"--max-attachment-mb: invalid value '{mb}'");
                    opts.MaxAttachmentBytes = parsed * 1024 * 1024;
                    break;
                case "--dry-run":
                    opts.DryRun = true;
                    break;
                case "--help" or "-h" or "/?":
                    throw new HelpRequestedException();
                default:
                    throw new ArgumentException($"Unknown option '{args[i]}' (use --help)");
            }
        }
        return opts;
    }
}

public sealed class HelpRequestedException : Exception;
