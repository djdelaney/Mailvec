namespace Mailvec.DevCorpus;

/// <summary>One file in the generated Maildir.</summary>
/// <param name="Folder">Folder path relative to the Maildir root, '/'-separated ("INBOX", "Archive/2025").</param>
/// <param name="Subdir">"cur" or "new".</param>
public sealed record MailFile(string Folder, string Subdir, string FileName, byte[] Bytes)
{
    public string RelativePath => $"{Folder}/{Subdir}/{FileName}";
}

/// <summary>
/// A named case the corpus exists to exercise, with the outcome the real
/// pipeline must produce for it. tests/Mailvec.DevCorpus.Tests indexes the
/// corpus and checks every expectation, so a scenario that stops meaning what
/// its description says fails there rather than silently.
/// </summary>
/// <param name="MessageId">Message-ID without angle brackets; null when the message deliberately has none.</param>
/// <param name="Subject">Decoded subject — also the lookup key when <paramref name="MessageId"/> is null.</param>
public sealed record Scenario(
    string Id,
    string Description,
    string? MessageId,
    string Subject,
    IReadOnlyList<MailFile> Files,
    Expect Expect);

public sealed record Expect
{
    /// <summary>Exact folder-membership set (sync_state) once indexed.</summary>
    public IReadOnlyList<string>? Folders { get; init; }
    public IReadOnlyList<string>? BodyContains { get; init; }
    public IReadOnlyList<string>? BodyExcludes { get; init; }
    /// <summary>Expected messages.thread_id.</summary>
    public string? ThreadId { get; init; }
    /// <summary>Expected instant of date_sent, or null to skip the check.</summary>
    public DateTimeOffset? DateSentUtc { get; init; }
    public bool DateSentNull { get; init; }
    public IReadOnlyList<AttachmentExpect>? Attachments { get; init; }
}

/// <param name="Status">An extraction_status value, verbatim ("done", "no_text", "encrypted", "unsupported", …).</param>
/// <param name="PartIndex">Expected part_index, or null to skip the check.</param>
public sealed record AttachmentExpect(string FileName, string Status, string? TextContains = null, int? PartIndex = null);

/// <summary>One labelled query in the eval set (the `mailvec eval` file format, version 1).</summary>
public sealed record EvalQuery(string Id, string Query, IReadOnlyList<string> Relevant, string? Folder = null, string? Notes = null);

/// <param name="Embedding">Null, or a hosted profile env.sh should configure (<see cref="HostedEmbedding.Names"/>).</param>
public sealed record CorpusOptions(bool Hazards = false, int Filler = Corpus.DefaultFiller, string? Embedding = null);

/// <summary>Everything one run produces, before any of it touches the disk.</summary>
public sealed record Corpus(
    IReadOnlyList<Scenario> Scenarios,
    IReadOnlyList<MailFile> Filler,
    IReadOnlyList<EvalQuery> Eval,
    IReadOnlyList<Hazard> Hazards)
{
    public const int DefaultFiller = 250;

    public IEnumerable<MailFile> AllMailFiles =>
        Scenarios.SelectMany(s => s.Files).Concat(Filler).Concat(Hazards.SelectMany(h => h.Files));
}

/// <summary>
/// A case Mailvec refuses or degrades on by design. Only written with
/// --hazards, so a plain corpus indexes clean. <paramref name="Outside"/> are
/// files written beside the Maildir root (never inside it), and
/// <paramref name="Symlinks"/> maps a Maildir-relative link path to a
/// corpus-relative target.
/// </summary>
public sealed record Hazard(
    string Id,
    string Description,
    IReadOnlyList<MailFile> Files,
    IReadOnlyDictionary<string, byte[]> Outside,
    IReadOnlyDictionary<string, string> Symlinks);
