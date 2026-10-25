namespace Mailvec.DevCorpus;

/// <summary>
/// How the corpus's folders are laid out on disk. <see cref="Default"/> is one
/// copy per message in Fastmail-style folders (folder mode: every message in
/// exactly one mailbox, except the two duplicate scenarios).
/// <see cref="Gmail"/> rewrites the same messages into the shape mbsync
/// leaves for a Gmail account synced with <c>Patterns *</c> and
/// <c>Subfolders Verbatim</c>: every label is a folder, and one message is a
/// file in each folder whose label it carries, <c>[Gmail]/All Mail</c> almost
/// always among them.
///
/// <para><b>The Gmail mapping is a belief, not a recording.</b> It encodes how
/// Gmail is documented to expose labels over IMAP; nothing here was observed
/// from a real account. tests/Mailvec.Indexer.Tests/ProviderTraces holds the
/// sequences (archive, relabel, trash, read-elsewhere) and is where recorded
/// traces replace hand-written ones.</para>
/// </summary>
public static class Layouts
{
    public const string Default = "default";
    public const string Gmail = "gmail";
    public static readonly IReadOnlyList<string> Names = [Default, Gmail];

    internal const string AllMail = "[Gmail]/All Mail";
    internal const string Important = "[Gmail]/Important";
    internal const string Starred = "[Gmail]/Starred";

    /// <summary>The scenario whose copies are all flagged and which also sits in Starred.</summary>
    internal const string StarredScenario = "s01-plain";

    /// <summary>
    /// The mailboxes a message filed in <paramref name="folder"/> lives in on Gmail.
    /// Trash is the one system folder outside All Mail (Spam is the other; the
    /// corpus has none). Archive has no Gmail counterpart: archiving removes
    /// the Inbox label, which leaves the message in All Mail alone.
    /// </summary>
    internal static IReadOnlyList<string> GmailMailboxes(string folder) => folder switch
    {
        "INBOX" => ["INBOX", AllMail],
        "Sent" => ["[Gmail]/Sent Mail", AllMail],
        "Drafts" => ["[Gmail]/Drafts", AllMail],
        "Trash" => ["[Gmail]/Trash"],
        "Archive" => [AllMail],
        _ => [folder, AllMail], // a user label, nested ones included ("Archive/2025", "Lists/rust-users")
    };

    /// <summary>
    /// The Gmail layout of <paramref name="corpus"/>: same messages, same bytes,
    /// one file per (message, mailbox). Scenario expectations are remapped the
    /// same way, so every one of them must still hold.
    /// </summary>
    internal static Corpus ToGmail(Corpus corpus)
    {
        var names = new CopyNames();
        var scenarios = corpus.Scenarios.Select(s =>
        {
            var important = s.Files.Any(f => f.Folder == "INBOX");
            var starred = s.Id == StarredScenario;
            var files = Remap(s.Files, names, important, starred);
            var expect = s.Expect.Folders is { } folders
                ? s.Expect with { Folders = files.Select(f => f.Folder).Distinct().Order(StringComparer.Ordinal).ToList() }
                : s.Expect;
            // A remapped expectation is only as good as the mapping, so check it
            // against the declared one rather than trusting the files alone.
            if (s.Expect.Folders is { } declared)
            {
                var mapped = declared.SelectMany(GmailMailboxes).ToHashSet(StringComparer.Ordinal);
                if (important) mapped.Add(Important);
                if (starred) mapped.Add(Starred);
                if (!mapped.SetEquals(expect.Folders!))
                    throw new InvalidOperationException($"{s.Id}: Gmail folders disagree with the remapped expectation.");
            }
            return s with { Files = files, Expect = expect };
        }).ToList();

        // Filler is background: no Important, no stars, one file per mailbox.
        var filler = corpus.Filler.SelectMany(f => Remap([f], names, important: false, starred: false)).ToList();
        // Hazards are about the filesystem, not the provider; they stay where they are.
        return corpus with { Scenarios = scenarios, Filler = filler };
    }

    private static List<MailFile> Remap(IReadOnlyList<MailFile> files, CopyNames names, bool important, bool starred)
    {
        var result = new List<MailFile>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            IEnumerable<string> mailboxes = GmailMailboxes(file.Folder);
            if (important && file.Folder == "INBOX") mailboxes = mailboxes.Append(Important);
            if (starred) mailboxes = mailboxes.Append(Starred);
            foreach (var mailbox in mailboxes)
            {
                // Two default-layout copies can land in the same mailbox (INBOX +
                // Archive both mean All Mail). Gmail holds one message once per
                // label, so the first copy wins — for the divergent pair that is
                // the Sent copy, which is also the one Gmail keeps.
                if (!taken.Add(mailbox)) continue;
                var flags = Flags(file.FileName);
                if (starred) flags = AddFlag(flags ?? "", 'F');
                // The first copy keeps its name, so the default layout's file
                // counter (and every name after it) is untouched.
                var name = result.Count == 0 && !starred ? file.FileName : names.Next(flags);
                result.Add(new MailFile(mailbox, flags is null ? "new" : "cur", name, file.Bytes));
            }
        }
        return result;
    }

    /// <summary>The info-suffix flags of an mbsync filename, or null for a new/ file.</summary>
    private static string? Flags(string fileName)
    {
        var at = fileName.IndexOf(":2,", StringComparison.Ordinal);
        return at < 0 ? null : fileName[(at + 3)..];
    }

    /// <summary>Maildir flags are kept in ASCII order.</summary>
    private static string AddFlag(string flags, char flag) =>
        flags.Contains(flag) ? flags : new string(flags.Append(flag).Order().ToArray());

    /// <summary>
    /// Names for the extra copies. Each IMAP folder has its own UID space, so a
    /// copy is a different file name, never the same name in another folder.
    /// A separate counter, so the default layout's names are unchanged.
    /// </summary>
    private sealed class CopyNames
    {
        private int _seq;

        public string Next(string? flags)
        {
            var n = ++_seq;
            var stem = $"{1767225600 + n * 7907}.M{n}P4343.devcorpus-gmail,U={n}";
            return flags is null ? stem : $"{stem}:2,{flags}";
        }
    }
}
