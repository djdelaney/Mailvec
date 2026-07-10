using System.Threading.Channels;
using Mailvec.Core;
using Mailvec.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailvec.Indexer.Services;

/// <summary>
/// Wraps a recursive FileSystemWatcher with a debounce so that mbsync's
/// tmp/ -> new/ rename burst arrives as a single trigger. Emits coalesced
/// "scan needed" pulses; the scanner is responsible for figuring out what
/// changed.
/// </summary>
public sealed class MaildirWatcher : IDisposable
{
    private readonly string _root;
    private readonly TimeSpan _debounce;
    private readonly ILogger<MaildirWatcher> _logger;
    private readonly Channel<byte> _pulses = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private FileSystemWatcher? _fsw;

    public MaildirWatcher(IOptions<IngestOptions> ingest, IOptions<IndexerOptions> indexer, ILogger<MaildirWatcher> logger)
    {
        _root = PathExpansion.Expand(ingest.Value.MaildirRoot);
        // Clamp like ScanIntervalSeconds gets clamped in MessageIngestService:
        // a negative value would make Task.Delay throw inside the unobserved
        // debounce task, leaving _debounceTask permanently non-null — every
        // future event would see a "running" loop and no pulse would ever
        // fire again (silent, timer-covered).
        _debounce = TimeSpan.FromMilliseconds(Math.Max(0, indexer.Value.DebounceMilliseconds));
        _logger = logger;
    }

    /// <summary>
    /// Pulses fire after a quiet period — the scanner should call ScanAll() each time.
    /// Cancellation closes the watcher.
    /// </summary>
    public ChannelReader<byte> Pulses => _pulses.Reader;

    // Test seam: lets tests substitute a factory that throws, simulating
    // Linux inotify exhaustion (IOException out of FSW creation).
    internal Func<string, FileSystemWatcher> CreateWatcher { get; set; } = root => new FileSystemWatcher(root);

    public void Start()
    {
        lock (_gate) { if (_fsw is not null) return; }
        if (!Directory.Exists(_root))
        {
            _logger.LogWarning("MaildirWatcher: {Path} does not exist; watcher disabled.", _root);
            return;
        }

        FileSystemWatcher? fsw = null;
        try
        {
            fsw = CreateWatcher(_root);
            fsw.IncludeSubdirectories = true;
            fsw.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName;
            fsw.Created  += (_, e) => OnEvent(e.FullPath);
            fsw.Deleted  += (_, e) => OnEvent(e.FullPath);
            fsw.Renamed  += (_, e) => OnEvent(e.FullPath);
            fsw.Changed  += (_, e) => OnEvent(e.FullPath);
            fsw.Error    += (sender, e) => HandleWatcherError((FileSystemWatcher)sender!, e.GetException());
            // Enable LAST, with every handler already attached, so an event
            // landing in the setup window can't be silently dropped.
            fsw.EnableRaisingEvents = true;
            lock (_gate) { _fsw = fsw; }
        }
        catch (Exception ex)
        {
            // Watcher creation/enable can throw — most plausibly IOException
            // on Linux when inotify max_user_watches / max_user_instances is
            // exhausted (IncludeSubdirectories registers a watch per Maildir
            // directory). Both Start() call sites sit on the service's spine:
            // an escaping throw stops the host, launchd/Docker restarts it
            // into the same condition, and the indexer crash-loops through a
            // full rescan each time. Log and stay watcher-less instead — the
            // periodic timer still drives scans, and the timer-tick Start()
            // retry brings the watcher up once the pressure clears.
            fsw?.Dispose();
            _logger.LogWarning(ex,
                "MaildirWatcher failed to start; falling back to timer-driven scans and retrying on the next tick.");
            return;
        }

        _logger.LogInformation("MaildirWatcher started on {Path} (debounce {Ms}ms)", _root, _debounce.TotalMilliseconds);
    }

    /// <summary>
    /// Error-event handler body (internal so tests can drive it — FSW gives
    /// no way to raise Error externally). An errored watcher may be
    /// permanently dead (buffer overflow recovers, but a deleted/remounted
    /// watch root does not, often without any further events), and Start()'s
    /// "already created" guard would then no-op on every timer tick — the
    /// exact retry that exists to bring the watcher back. Force a full-pass
    /// pulse for the dropped events, then retire this instance so the next
    /// tick recreates it.
    /// </summary>
    internal void HandleWatcherError(FileSystemWatcher dead, Exception? cause)
    {
        _logger.LogWarning(cause, "FileSystemWatcher reported an error; forcing a rescan and retiring the watcher for recreation");
        _pulses.Writer.TryWrite(0);

        lock (_gate)
        {
            // A late error from an already-replaced instance must not tear
            // down its replacement.
            if (!ReferenceEquals(_fsw, dead)) return;
            _fsw = null;
        }
        // Dispose off this thread: disposing an FSW from inside its own
        // event callback can deadlock on the callback it is running.
        _ = Task.Run(() =>
        {
            try { dead.Dispose(); }
            catch { /* best effort — it is already broken */ }
        });
    }

    private DateTimeOffset _lastEventAt;
    private readonly Lock _gate = new();
    private Task? _debounceTask;

    private void OnEvent(string fullPath)
    {
        // Ignore events whose path inside the watched root contains a tmp/
        // segment — mbsync writes there before the atomic rename into new/.
        // Compare against the path *relative to _root* so a watcher rooted
        // under a directory that happens to contain "/tmp/" (e.g. macOS
        // $TMPDIR=/tmp/<user>/ during tests) doesn't filter every event.
        if (IsInsideMbsyncTmp(fullPath))
            return;

        lock (_gate)
        {
            _lastEventAt = DateTimeOffset.UtcNow;
            _debounceTask ??= Task.Run(DebounceLoopAsync);
        }
    }

    /// <summary>
    /// Visible for testing. True iff <paramref name="fullPath"/> is the
    /// <c>tmp</c> directory of some Maildir bucket under <paramref name="root"/>,
    /// or a file inside one. Substring-matching the absolute path was wrong
    /// because the root itself can legitimately live under <c>/tmp/</c>.
    /// </summary>
    internal static bool IsInsideMbsyncTmp(string fullPath, string root)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        // GetRelativePath returns the absolute path back if the file isn't
        // under root — treat that as "not inside tmp" (we'd ignore it for
        // other reasons; the watcher shouldn't see such events).
        if (Path.IsPathRooted(rel)) return false;
        foreach (var segment in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment == "tmp") return true;
        }
        return false;
    }

    private bool IsInsideMbsyncTmp(string fullPath) => IsInsideMbsyncTmp(fullPath, _root);

    private async Task DebounceLoopAsync()
    {
        while (true)
        {
            await Task.Delay(_debounce).ConfigureAwait(false);

            DateTimeOffset last;
            lock (_gate) { last = _lastEventAt; }

            if (DateTimeOffset.UtcNow - last >= _debounce)
            {
                _pulses.Writer.TryWrite(0);
                lock (_gate)
                {
                    // An event may have landed between the quiet-check above
                    // and taking the gate here. Exiting now would strand it:
                    // its OnEvent saw a live loop and started no new one, and
                    // the pulse just written may be consumed by a scan that
                    // enumerates before the new file lands — leaving the
                    // change unscanned until the periodic timer. Keep looping
                    // until the quiet period covers the newest event observed
                    // under the gate.
                    if (_lastEventAt > last) continue;
                    _debounceTask = null;
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        _fsw?.Dispose();
        _pulses.Writer.TryComplete();
    }
}
