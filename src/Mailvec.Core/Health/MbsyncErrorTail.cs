using System.Text.RegularExpressions;

namespace Mailvec.Core.Health;

/// <summary>
/// Tails <c>~/Library/Logs/Mailvec/mailvec-mbsync.err.log</c> and reports
/// whether mbsync has written errors recently enough to matter.
///
/// Why this exists: mbsync is invoked by launchd as a one-shot scheduled
/// command, so its exit status is EPHEMERAL — launchd reports the last run's
/// code and nothing retains it, while the error detail is what an operator
/// actually needs to act. The stderr log is the only durable record of *what*
/// went wrong. We read the last few KB, look for lines that start with
/// "Error:" or "Socket error", and surface the most recent one as a
/// service-status detail string.
///
/// <para>This used to justify itself by claiming mbsync "exits 0" on a failed
/// sync, making the exit code useless. It does not: upstream propagates sync
/// errors through a nonzero return, which is exactly what the container loop
/// depends on (<c>if [ "$rc" -eq 0 ]</c> gates the sync-success marker). The
/// premise was false; the conclusion — read stderr — happens to be right for
/// the ephemerality reason above. Don't reintroduce the exit-0 claim.</para>
///
/// Recency: we treat an error as "live" if it was written within roughly
/// 2× the configured StartInterval. One missed run is bad; older errors
/// are historical and don't deserve to colour the tile.
/// </summary>
public sealed class MbsyncErrorTail(IMbsyncErrorTailClock? clock = null)
{
    /// <summary>Default location matches <c>ops/launchd/com.mailvec.mbsync.plist</c>.</summary>
    public const string DefaultLogPath = "~/Library/Logs/Mailvec/mailvec-mbsync.err.log";
    public const string DefaultPlistPath = "~/Library/LaunchAgents/com.mailvec.mbsync.plist";

    /// <summary>Fallback used when the plist can't be read — matches the install template.</summary>
    private const int DefaultStartIntervalSeconds = 600;

    /// <summary>Tail this many bytes from the end of the file. ~16KB is well over
    /// "the last few mbsync runs"; reading more wastes IO without adding value.</summary>
    private const int TailBytes = 16 * 1024;

    private readonly IMbsyncErrorTailClock _clock = clock ?? new SystemClock();

    /// <summary>
    /// Reads the err log and returns the most recent error if one falls
    /// inside the freshness window, else null.
    /// </summary>
    public MbsyncError? CheckRecent(string? logPath = null, string? plistPath = null, string? outLogPath = null)
    {
        try
        {
            var resolvedLog = PathExpansion.Expand(logPath ?? DefaultLogPath);
            if (!File.Exists(resolvedLog)) return null;

            // Freshness threshold: 2× the configured StartInterval, with a
            // floor of two minutes so a manually-edited 30s interval doesn't
            // produce a freshness window so tight that the status flaps.
            var intervalSeconds = ReadStartIntervalSeconds(plistPath ?? DefaultPlistPath);
            var windowSeconds = Math.Max(intervalSeconds * 2, 120);
            var now = _clock.UtcNow;

            var info = new FileInfo(resolvedLog);
            if (info.Length == 0) return null;
            if ((now - info.LastWriteTimeUtc).TotalSeconds > windowSeconds) return null;

            // Positive recovery signal. mbsync writes run progress to its
            // stdout log on a successful run but nothing to stderr. If the
            // stdout log was touched *more recently* than this stderr log, the
            // latest run succeeded with no new error — treat the stale error
            // as resolved. This drops the tile back to green on the next good
            // sync (≤ one interval, or immediately after a manual kickstart)
            // instead of waiting out the full freshness window. A failed run
            // appends to stderr and bumps this log's mtime past stdout, so the
            // error correctly reappears.
            var resolvedOut = PathExpansion.Expand(outLogPath ?? DeriveOutLogPath(logPath ?? DefaultLogPath));
            if (File.Exists(resolvedOut))
            {
                var outInfo = new FileInfo(resolvedOut);
                if (outInfo.Length > 0 && outInfo.LastWriteTimeUtc > info.LastWriteTimeUtc) return null;
            }

            // Tail the file. mbsync emits one error per line, so we can
            // safely read the last few KB and split on newlines.
            using var stream = new FileStream(
                resolvedLog,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var seek = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(seek, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            // The most recent error line wins. mbsync doesn't timestamp
            // its stderr lines, so we have to attribute all of them to
            // the file's mtime — coarse but accurate enough at minute
            // granularity, which is all any consumer displays.
            string? lastError = null;
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (LooksLikeError(line)) lastError = line;
            }
            if (lastError is null) return null;

            return new MbsyncError(
                Message: lastError,
                Kind: ClassifyError(lastError),
                ObservedAt: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                WindowSeconds: windowSeconds);
        }
        catch
        {
            // Permission / IO / parse failures are non-fatal — we'd
            // rather show a stale-but-green tile than crash the status
            // endpoint. The launchd exit-code check still runs.
            return null;
        }
    }

    /// <summary>
    /// Reads <c>StartInterval</c> out of the mbsync launchd plist. Falls back
    /// to 600s (the install-template default) when the plist is missing or
    /// malformed.
    /// </summary>
    /// <summary>
    /// Best-effort "last successful sync" timestamp: the mtime of mbsync's
    /// stdout log. mbsync writes run progress to stdout on a run that produced
    /// output, so this advances on each successful sync — the closest signal
    /// we have to a completion time without parsing mbsync's progress lines.
    /// Returns null when the log is missing or empty (never synced). This is
    /// the same file the stdout-mtime recovery check in
    /// <see cref="CheckRecent"/> reads, so the dashboard's "last sync" display
    /// and the tile's recovery stay sourced from one signal.
    /// </summary>
    public DateTimeOffset? LastSuccessfulSyncAt(string? outLogPath = null)
    {
        try
        {
            var resolved = PathExpansion.Expand(outLogPath ?? DeriveOutLogPath(DefaultLogPath));
            if (!File.Exists(resolved)) return null;
            var info = new FileInfo(resolved);
            if (info.Length == 0) return null;
            return new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Derives the stdout log path that sits next to the stderr log. The
    /// launchd plist writes <c>mailvec-mbsync.out.log</c> alongside
    /// <c>mailvec-mbsync.err.log</c>; callers only hand us the latter. Falls
    /// back to the input path for non-standard names, which makes the
    /// stdout-mtime check a harmless self-compare (mtime is never &gt; itself).
    /// </summary>
    private static string DeriveOutLogPath(string errLogPath) =>
        errLogPath.EndsWith(".err.log", StringComparison.Ordinal)
            ? errLogPath[..^".err.log".Length] + ".out.log"
            : errLogPath;

    private static int ReadStartIntervalSeconds(string plistPath)
    {
        try
        {
            var path = PathExpansion.Expand(plistPath);
            if (!File.Exists(path)) return DefaultStartIntervalSeconds;
            var xml = File.ReadAllText(path);
            var m = Regex.Match(
                xml,
                @"<key>\s*StartInterval\s*</key>\s*<integer>\s*(\d+)\s*</integer>",
                RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out var seconds)) return DefaultStartIntervalSeconds;
            return seconds;
        }
        catch
        {
            return DefaultStartIntervalSeconds;
        }
    }

    private static bool LooksLikeError(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        // mbsync's stderr error patterns. We match prefixes only — line
        // content past the prefix carries the human-readable detail.
        return line.StartsWith("Error:", StringComparison.Ordinal)
            || line.StartsWith("IMAP error:", StringComparison.Ordinal)
            || line.StartsWith("Socket error", StringComparison.Ordinal)
            || line.StartsWith("Maildir error:", StringComparison.Ordinal);
    }

    /// <summary>
    /// Categorises an mbsync error line into a stable kind tag that
    /// <c>mailvec doctor</c> branches on to pick its remediation hint —
    /// don't rename existing values without updating that switch.
    /// </summary>
    private static MbsyncErrorKind ClassifyError(string line)
    {
        // Most operationally important: a left-behind .mbsyncstate.lock
        // blocks every subsequent run until cleared. User action required.
        if (line.Contains("is locked", StringComparison.OrdinalIgnoreCase))
            return MbsyncErrorKind.Locked;

        // DNS failure — almost always a network outage at the user's
        // machine; clears itself when connectivity returns.
        if (line.Contains("Cannot resolve", StringComparison.OrdinalIgnoreCase)
            || line.Contains("nodename nor servname", StringComparison.OrdinalIgnoreCase))
            return MbsyncErrorKind.Dns;

        // Transient TCP failures — connection reset, timeout, certificate
        // errors. Usually clears within a run or two.
        if (line.StartsWith("Socket error", StringComparison.Ordinal)
            || line.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Connection reset", StringComparison.OrdinalIgnoreCase))
            return MbsyncErrorKind.Network;

        // Auth failures — user action required (rotate app password).
        if (line.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || line.Contains("LOGIN failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("AUTHENTICATE failed", StringComparison.OrdinalIgnoreCase))
            return MbsyncErrorKind.Auth;

        return MbsyncErrorKind.Other;
    }

    private sealed class SystemClock : IMbsyncErrorTailClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }
}

/// <summary>
/// Snapshot of the most recent in-window mbsync stderr error. Returned by
/// <see cref="MbsyncErrorTail.CheckRecent(string?, string?)"/> or null when
/// the log is silent / stale.
/// </summary>
public sealed record MbsyncError(
    string Message,
    MbsyncErrorKind Kind,
    DateTimeOffset ObservedAt,
    int WindowSeconds);

public enum MbsyncErrorKind
{
    Locked,
    Dns,
    Network,
    Auth,
    Other,
}

/// <summary>Tests stub this to advance "now" without sleeping.</summary>
public interface IMbsyncErrorTailClock
{
    DateTime UtcNow { get; }
}
