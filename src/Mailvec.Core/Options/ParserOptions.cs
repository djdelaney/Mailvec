namespace Mailvec.Core.Options;

/// <summary>
/// Where mail content gets parsed. <c>inprocess</c> (the default, and the
/// macOS launchd install) runs the parsers inside the calling process;
/// <c>remote</c> (the container deployment) sends bytes to the <c>parse</c>
/// service at <see cref="Endpoint"/>. Resolved centrally by
/// <c>ParserRegistration.AddMailvecParser</c>; an unknown mode is fatal, and
/// so is remote mode without an endpoint.
///
/// The <c>parse</c> host reads the same <c>Parser:*</c> section for ITS
/// settings (bind address, its own per-request timeout, request cap, exit
/// budget) — see <c>Mailvec.Parse.ParseHostOptions</c>. The two sides are
/// deliberately separate classes: the host must not reference Core.
/// </summary>
public sealed class ParserOptions
{
    public const string SectionName = "Parser";

    /// <summary>"inprocess" or "remote".</summary>
    public string Mode { get; set; } = "inprocess";

    /// <summary>Base URL of the parse service; required when <see cref="Mode"/> is remote.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Client-side ceiling on one parse call. Deliberately LONGER than the
    /// host's own <c>RequestTimeoutSeconds</c> (60 s): the host answers 504 and
    /// exits when a document overruns, which the client classifies as a strike
    /// against that document; reaching this timeout instead means the host
    /// never answered at all, which is classified as "unavailable" and counts
    /// against nothing.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// How many times one file (same path, mtime and size) may crash the
    /// parser (<c>ParseFailureKind.Crashed</c>: a timeout-and-exit, an OOM
    /// kill, a 5xx) before the indexer stops asking for its attachment text
    /// and indexes the message with its attachments at
    /// <c>extraction_status='failed'</c>. Counted in memory by
    /// <c>MaildirScanner</c>, so a restart grants another round; the message
    /// is indexed either way, only the poison document's text is given up.
    /// </summary>
    public int MaxCrashesPerFile { get; set; } = 3;
}
