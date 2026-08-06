using System.Globalization;
using Mailvec.Core.Options;
using Microsoft.Extensions.Options;

namespace Mailvec.Core.Health;

/// <summary>
/// Reads mbsync's last-successful-sync marker — the sidecar's batch-OUTCOME
/// signal, distinct from the liveness beat <see cref="MbsyncHeartbeatFile"/>
/// reads.
///
/// <para><b>Why this is a third signal and not a field on the beat.</b> The
/// beat answers "is the sidecar alive", and it is deliberately written on its
/// own timer regardless of whether <c>mbsync -a</c> succeeded — a loop retrying
/// against a dead IMAP server is alive, and reporting it as dead sends you
/// hunting a stopped container that is running fine. The cost of that
/// correctness is a blind spot: a sidecar whose every sync fails (expired app
/// password, a <c>Patterns</c> typo, DNS gone) beats happily forever while no
/// mail arrives. Nothing else closes it — <c>messages.indexed_at</c> only moves
/// when new mail is actually ingested, so "quiet mailbox" and "sync broken"
/// are indistinguishable there. This file is the missing fact, and it is the
/// same three-way split <see cref="ServiceHeartbeat"/> already documents:
/// liveness, progress, outcome. Don't collapse it back into the beat.</para>
///
/// <para><b>Why a second file rather than more lines in the beat.</b> The
/// sidecar's <c>beat()</c> runs inside a backgrounded subshell on its own
/// timer, and a subshell cannot see variables the parent loop assigns after it
/// forked. Carrying sync outcome in the beat would mean the parent writing it
/// somewhere the beater can read — i.e. this file, with extra steps. Two
/// writers, two files, two independent facts.</para>
///
/// <para><b>Location matters, identically to the beat</b>: the Maildir
/// <i>parent</i> (<c>/mail/.mailvec-mbsync-sync</c>), never inside the root,
/// because <c>MaildirScanner</c> walks that root and Maildir++ names folders
/// with a leading dot — a dotfile in the tree risks being parsed as a folder.
/// It also means the marker lives on the bind mount and survives container
/// recreation, so a sidecar that comes back and keeps failing keeps ageing its
/// real last-success time rather than resetting to unknown.</para>
///
/// <para><b>Format</b>: two lines — ISO-8601 UTC, then the sidecar's SYNC
/// interval in seconds. Here the sync cadence is the right thing to declare
/// (unlike in the beat file, where coupling the two was the bug fixed in
/// 6192314): this file's staleness genuinely is a multiple of how often a sync
/// is attempted.</para>
///
/// <para><b>Absent reports unknown, never stale.</b> A fresh deployment, and
/// every macOS launchd install (no sidecar writes this file at all), must not
/// show a permanent false red — the same rule the beat and the metadata-backed
/// services follow. The cost is a real gap: a deployment whose sync has NEVER
/// succeeded reads unknown rather than broken, exactly as a worker that never
/// starts reads <c>Known=false</c> rather than stale. Consistency wins here;
/// a false red on first boot is what teaches an operator to ignore the
/// indicator.</para>
/// </summary>
public sealed class MbsyncSyncFile(IOptions<IngestOptions> ingest)
{
    public const string FileName = ".mailvec-mbsync-sync";

    /// <summary>
    /// How many sync intervals may pass with no success before this reads
    /// stale.
    /// </summary>
    /// <remarks>
    /// Its own constant, NOT <see cref="ServiceHeartbeat.StaleAfterMissedBeats"/>.
    /// That one is shared with the indexer and embedder, and retuning it to
    /// suit this signal would degrade dead-worker detection everywhere to
    /// paper over one sidecar — the exact trap 6192314's message calls out.
    ///
    /// 4 rather than 3 because the quantity being bounded is the time BETWEEN
    /// successes, not the interval: a 12-minute backlog pull plus the 600s
    /// interval is already 22 minutes, and one failed cycle in between pushes
    /// past a 30-minute window. At the 600s default this gives 40 minutes,
    /// i.e. roughly three consecutive failed cycles before alerting.
    /// </remarks>
    public const int StaleAfterMissedCycles = 4;

    /// <summary>
    /// Floor on the staleness window, regardless of how short the sync
    /// interval is.
    /// </summary>
    /// <remarks>
    /// Without it, the window collapses with the interval while the thing it
    /// has to accommodate — how long a real backlog pull takes — does not.
    /// docs/future-ideas.md plans a one-minute sync cadence; at that setting a
    /// bare multiple would be a 4-minute window, and any pull slower than that
    /// would report a working sidecar as broken. Same class of bug as the beat
    /// cadence being wired to the sync interval, so it gets the same guard.
    /// </remarks>
    public static readonly TimeSpan MinStaleWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Resolved marker path: the sibling of the configured Maildir root. Null
    /// when the root has no parent (a Maildir at a filesystem root —
    /// nonsensical, but don't throw over it).
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

    public MailHealth Read(DateTimeOffset? now = null)
    {
        var path = Path;
        if (path is null || !File.Exists(path)) return Unknown;

        try
        {
            // Two short lines. The writer builds a .tmp sibling and mv's it
            // into place, so a reader never observes a half-written marker; a
            // malformed one still degrades to unknown rather than throwing —
            // /health is the mcp container's own compose healthcheck, and an
            // exception here would restart-loop it.
            var lines = File.ReadAllLines(path);
            var at = lines.Length > 0 && DateTimeOffset.TryParse(
                lines[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                ? t
                : (DateTimeOffset?)null;
            var interval = lines.Length > 1 && int.TryParse(
                lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i > 0
                ? i
                : (int?)null;

            return Classify(at, interval, now);
        }
        catch (IOException)
        {
            return Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return Unknown;
        }
    }

    private static MailHealth Unknown => new(null, null, SyncStale: false, Known: false);

    /// <summary>
    /// How long without a successful sync before this reads stale, for a
    /// sidecar declaring <paramref name="intervalSeconds"/>. Public so callers
    /// that explain the verdict (`mailvec doctor`) quote the same threshold the
    /// verdict was made with, rather than re-deriving it and drifting.
    /// </summary>
    public static TimeSpan StaleWindow(int intervalSeconds)
    {
        var window = TimeSpan.FromSeconds((double)intervalSeconds * StaleAfterMissedCycles);
        return window < MinStaleWindow ? MinStaleWindow : window;
    }

    internal static MailHealth Classify(DateTimeOffset? lastSyncAt, int? intervalSeconds, DateTimeOffset? now = null)
    {
        if (lastSyncAt is null || intervalSeconds is null) return Unknown;

        var nowUtc = now ?? DateTimeOffset.UtcNow;
        return new MailHealth(
            lastSyncAt,
            intervalSeconds,
            nowUtc - lastSyncAt.Value > StaleWindow(intervalSeconds.Value),
            Known: true);
    }
}
