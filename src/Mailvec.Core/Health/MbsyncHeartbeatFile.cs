using System.Globalization;
using Mailvec.Core.Options;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Health;

/// <summary>
/// Reads mbsync's liveness beat.
///
/// <para><b>Why a file and not the metadata table.</b> mbsync runs as a POSIX
/// <c>/bin/sh</c> loop in an Alpine sidecar (Dockerfile stage <c>mbsync</c>) —
/// no .NET, no SQLite client, and adding either to get one timestamp across
/// would be absurd. It already shares the Maildir bind mount with every other
/// service, so a file there is the cheapest channel that exists. Written by
/// <c>mbsync-loop</c> after each cycle, read here.</para>
///
/// <para><b>Location matters.</b> The beat sits at the Maildir <i>parent</i>
/// (<c>/mail/.mailvec-mbsync-heartbeat</c>, next to <c>/mail/Fastmail</c>) and
/// NOT inside the Maildir root, because <c>MaildirScanner</c> walks that root
/// and Maildir++ names its folders with a leading dot — a dotfile inside the
/// tree risks being read as a folder. Outside the root it is invisible to the
/// scanner. If you move <c>Ingest:MaildirRoot</c> to the mount point itself,
/// this assumption breaks.</para>
///
/// <para><b>Format</b>: two lines — an ISO-8601 UTC timestamp, then the
/// sidecar's interval in seconds. The interval travels with the beat for the
/// same reason it does for the metadata-backed services: the reader shouldn't
/// need to know the writer's config (<c>MBSYNC_INTERVAL_SECONDS</c> is set on
/// the sidecar, which the MCP container can't see). File mtime is deliberately
/// not the signal — it's a bind mount across a container boundary, and content
/// we control is more predictable than mtime semantics we don't.</para>
///
/// <para>On the macOS launchd install, mbsync writes no such file (its beat
/// path is <c>MbsyncErrorTail</c> over the agent's logs). Absence reports as
/// unknown rather than stale, so the Mac dev install doesn't show a false
/// red.</para>
/// </summary>
public sealed class MbsyncHeartbeatFile(IOptions<IngestOptions> ingest)
{
    public const string Service = "mbsync";
    public const string FileName = ".mailvec-mbsync-heartbeat";

    /// <summary>
    /// Resolved beat path: the sibling of the configured Maildir root. Null
    /// when the root has no parent (a Maildir configured at a filesystem root
    /// — nonsensical, but don't throw over it).
    /// </summary>
    public string? Path
    {
        get
        {
            var root = PathExpansion.Expand(ingest.Value.MaildirRoot);
            if (string.IsNullOrWhiteSpace(root)) return null;
            var parent = System.IO.Path.GetDirectoryName(root.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            return string.IsNullOrEmpty(parent) ? null : System.IO.Path.Combine(parent, FileName);
        }
    }

    /// <summary>
    /// mbsync reports one timestamp, used as both liveness and cycle: the
    /// sidecar's loop writes the beat after every sync attempt, so "the loop
    /// is turning" and "the process is alive" are the same fact here. It beats
    /// on a failed sync too — a loop retrying against a dead IMAP server is
    /// alive, and that failure surfaces in <c>docker logs</c>, not as a fake
    /// death.
    /// </summary>
    public ServiceLiveness Read(DateTimeOffset? now = null)
    {
        var path = Path;
        if (path is null || !File.Exists(path))
            return ServiceHeartbeat.Classify(Service, null, null, null, now);

        try
        {
            // Two short lines. The writer builds a .tmp sibling and mv's it
            // into place, so a reader never observes a half-written beat; a
            // malformed one still degrades to unknown rather than throwing.
            var lines = File.ReadAllLines(path);
            var at = lines.Length > 0 && DateTimeOffset.TryParse(
                lines[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                ? t
                : (DateTimeOffset?)null;
            var interval = lines.Length > 1 && int.TryParse(
                lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i > 0
                ? i
                : (int?)null;

            return ServiceHeartbeat.Classify(Service, at, at, interval, now);
        }
        catch (IOException)
        {
            // Mid-write, or the mount went away. Unknown beats a false red.
            return ServiceHeartbeat.Classify(Service, null, null, null, now);
        }
        catch (UnauthorizedAccessException)
        {
            return ServiceHeartbeat.Classify(Service, null, null, null, now);
        }
    }
}
