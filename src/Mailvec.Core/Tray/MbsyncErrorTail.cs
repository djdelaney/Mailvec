using System.Text.RegularExpressions;

namespace Mailvec.Core.Tray;

/// <summary>
/// Tails <c>~/Library/Logs/Mailvec/mailvec-mbsync.err.log</c> and reports
/// whether mbsync has written errors recently enough to matter.
///
/// Why this exists: mbsync is invoked by launchd as a one-shot scheduled
/// command. When it hits a stuck state (notably "channel … is locked" when
/// a previous run left a stale .mbsyncstate.lock around), it writes the
/// error to stderr and *exits 0*. That makes the launchctl-reported exit
/// code a useless signal — the tray's mbsync tile stays green while every
/// scheduled run aborts. We've seen this break sync silently for hours.
///
/// The honest source of truth is the stderr log itself. We read the last
/// few KB, look for lines that start with "Error:" or "Socket error",
/// and surface the most recent one as a service-status detail string.
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
    public MbsyncError? CheckRecent(string? logPath = null, string? plistPath = null)
    {
        try
        {
            var resolvedLog = PathExpansion.Expand(logPath ?? DefaultLogPath);
            if (!File.Exists(resolvedLog)) return null;

            // Freshness threshold: 2× the configured StartInterval, with a
            // floor of two minutes so a manually-edited 30s interval doesn't
            // produce a freshness window so tight that the tray flickers.
            var intervalSeconds = ReadStartIntervalSeconds(plistPath ?? DefaultPlistPath);
            var windowSeconds = Math.Max(intervalSeconds * 2, 120);
            var now = _clock.UtcNow;

            var info = new FileInfo(resolvedLog);
            if (info.Length == 0) return null;
            if ((now - info.LastWriteTimeUtc).TotalSeconds > windowSeconds) return null;

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
            // granularity, which is what the tray UI displays.
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
    /// Reads <c>StartInterval</c> out of the mbsync launchd plist. Duplicates
    /// the regex used by <see cref="TraySystemService"/> so this helper has
    /// no upward dependency on that service. Falls back to 600s (the
    /// install-template default) when the plist is missing or malformed.
    /// </summary>
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
    /// Categorises an mbsync error line into a stable kind tag that the
    /// tray + doctor can branch on. The kind enum is part of the contract
    /// with the tray — don't rename existing values.
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
