using Mailvec.Core.Tray;

namespace Mailvec.Core.Tests.Tray;

/// <summary>
/// Verifies the mbsync stderr tail surfaces the right kinds of errors and
/// honours the freshness window. The "freshness" check is what stops a
/// log file from a network outage two weeks ago turning the tray tile
/// yellow today.
///
/// All tests write to a tmp file and inject a fake clock so we don't rely
/// on wall-clock timing or the real LaunchAgents plist on the test machine.
/// </summary>
public sealed class MbsyncErrorTailTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"mbsync-test-{Guid.NewGuid():N}.log");
    private readonly string _plistPath = Path.Combine(Path.GetTempPath(), $"mbsync-test-{Guid.NewGuid():N}.plist");

    public void Dispose()
    {
        if (File.Exists(_logPath)) File.Delete(_logPath);
        if (File.Exists(_plistPath)) File.Delete(_plistPath);
    }

    private void WritePlistWithInterval(int seconds)
    {
        File.WriteAllText(_plistPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0">
              <dict>
                <key>StartInterval</key>
                <integer>{seconds}</integer>
              </dict>
            </plist>
            """);
    }

    private void WriteLog(string content, DateTime? mtimeUtc = null)
    {
        File.WriteAllText(_logPath, content);
        if (mtimeUtc.HasValue) File.SetLastWriteTimeUtc(_logPath, mtimeUtc.Value);
    }

    [Fact]
    public void Missing_log_returns_null()
    {
        var tail = new MbsyncErrorTail(new FixedClock(DateTime.UtcNow));
        var result = tail.CheckRecent(_logPath, _plistPath);
        Assert.Null(result);
    }

    [Fact]
    public void Empty_log_returns_null()
    {
        WriteLog(string.Empty);
        var tail = new MbsyncErrorTail(new FixedClock(DateTime.UtcNow));
        Assert.Null(tail.CheckRecent(_logPath, _plistPath));
    }

    [Fact]
    public void Old_error_outside_window_returns_null()
    {
        // 10-minute schedule → 20-minute window. Mtime is 1 hour ago, so
        // the log shouldn't move the tile even though it contains errors.
        WritePlistWithInterval(600);
        var now = new DateTime(2026, 5, 17, 9, 0, 0, DateTimeKind.Utc);
        WriteLog("Error: channel :fastmail-remote:INBOX-:fastmail-local:INBOX is locked\n",
                 mtimeUtc: now.AddHours(-1));
        var tail = new MbsyncErrorTail(new FixedClock(now));
        Assert.Null(tail.CheckRecent(_logPath, _plistPath));
    }

    [Fact]
    public void Recent_lock_error_is_classified_as_Locked()
    {
        WritePlistWithInterval(600);
        var now = new DateTime(2026, 5, 17, 9, 0, 0, DateTimeKind.Utc);
        WriteLog("Error: channel :fastmail-remote:INBOX-:fastmail-local:INBOX is locked\n",
                 mtimeUtc: now.AddMinutes(-2));
        var tail = new MbsyncErrorTail(new FixedClock(now));
        var result = tail.CheckRecent(_logPath, _plistPath);
        Assert.NotNull(result);
        Assert.Equal(MbsyncErrorKind.Locked, result!.Kind);
        Assert.Contains("is locked", result.Message);
    }

    [Fact]
    public void Recent_DNS_error_is_classified_as_Dns()
    {
        WritePlistWithInterval(600);
        var now = new DateTime(2026, 5, 17, 9, 0, 0, DateTimeKind.Utc);
        WriteLog("Error: Cannot resolve server 'imap.fastmail.com': nodename nor servname provided, or not known\n",
                 mtimeUtc: now.AddMinutes(-2));
        var tail = new MbsyncErrorTail(new FixedClock(now));
        var result = tail.CheckRecent(_logPath, _plistPath);
        Assert.Equal(MbsyncErrorKind.Dns, result?.Kind);
    }

    [Fact]
    public void Socket_error_is_classified_as_Network()
    {
        WritePlistWithInterval(600);
        var now = new DateTime(2026, 5, 17, 9, 0, 0, DateTimeKind.Utc);
        WriteLog("Socket error: secure read from imap.fastmail.com (1.2.3.4:993): Connection reset by peer\n",
                 mtimeUtc: now.AddMinutes(-2));
        var tail = new MbsyncErrorTail(new FixedClock(now));
        var result = tail.CheckRecent(_logPath, _plistPath);
        Assert.Equal(MbsyncErrorKind.Network, result?.Kind);
    }

    [Fact]
    public void Unknown_error_is_classified_as_Other()
    {
        WritePlistWithInterval(600);
        var now = new DateTime(2026, 5, 17, 9, 0, 0, DateTimeKind.Utc);
        WriteLog("Error: Maildir corruption: malformed header\n",
                 mtimeUtc: now.AddMinutes(-2));
        var tail = new MbsyncErrorTail(new FixedClock(now));
        var result = tail.CheckRecent(_logPath, _plistPath);
        Assert.Equal(MbsyncErrorKind.Other, result?.Kind);
    }

    [Fact]
    public void Most_recent_error_wins_when_log_has_history()
    {
        // Several different errors in one log; the LAST one should
        // determine the returned kind. The whole log is "fresh" because
        // mbsync rewrites the file on each run.
        WritePlistWithInterval(600);
        var now = new DateTime(2026, 5, 17, 9, 0, 0, DateTimeKind.Utc);
        WriteLog("""
            Error: Cannot resolve server 'imap.fastmail.com'
            Socket error on imap.fastmail.com (1.2.3.4:993): timeout.
            Error: channel :fastmail-remote:INBOX-:fastmail-local:INBOX is locked
            """,
            mtimeUtc: now.AddMinutes(-1));
        var tail = new MbsyncErrorTail(new FixedClock(now));
        var result = tail.CheckRecent(_logPath, _plistPath);
        Assert.Equal(MbsyncErrorKind.Locked, result?.Kind);
    }

    [Fact]
    public void Non_error_lines_are_ignored()
    {
        // mbsync's stdout / stderr can include progress lines; we only
        // care about lines starting with "Error:" / "Socket error" / etc.
        WritePlistWithInterval(600);
        var now = new DateTime(2026, 5, 17, 9, 0, 0, DateTimeKind.Utc);
        WriteLog("Channels: 1    Boxes: 1    Far: +0 *0 #0 -0    Near: +0 *0 #0 -0\n",
                 mtimeUtc: now.AddMinutes(-1));
        var tail = new MbsyncErrorTail(new FixedClock(now));
        Assert.Null(tail.CheckRecent(_logPath, _plistPath));
    }

    [Fact]
    public void Missing_plist_falls_back_to_default_interval()
    {
        // No plist at the configured path — should still work using the
        // 600s default, which produces a 1200s window.
        var now = new DateTime(2026, 5, 17, 9, 0, 0, DateTimeKind.Utc);
        WriteLog("Error: channel is locked\n", mtimeUtc: now.AddMinutes(-5));
        var tail = new MbsyncErrorTail(new FixedClock(now));
        // _plistPath was never created.
        var result = tail.CheckRecent(_logPath, _plistPath);
        Assert.NotNull(result);
    }

    [Fact]
    public void Window_floor_is_two_minutes()
    {
        // Even with a 30-second StartInterval the window shouldn't shrink
        // below 120s — otherwise a single delayed write would flicker the
        // tile off mid-flight.
        WritePlistWithInterval(30);
        var now = new DateTime(2026, 5, 17, 9, 0, 0, DateTimeKind.Utc);
        WriteLog("Error: channel is locked\n", mtimeUtc: now.AddSeconds(-90));
        var tail = new MbsyncErrorTail(new FixedClock(now));
        Assert.NotNull(tail.CheckRecent(_logPath, _plistPath));
    }

    private sealed class FixedClock(DateTime now) : IMbsyncErrorTailClock
    {
        public DateTime UtcNow { get; } = now;
    }
}
