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

    // Second test seam, for the same reason as CreateWatcher: FileSystemWatcher
    // gives no way to make enabling fail or to raise an Error at exactly that
    // moment, and that instant is the one window where publication order used
    // to matter. EnableRaisingEvents is not virtual, so a subclass cannot
    // intercept it — hence a hook rather than a fake type.
    internal Action<FileSystemWatcher> EnableWatcher { get; set; } = w => w.EnableRaisingEvents = true;

    /// <summary>
    /// Bring the watcher up, if it isn't already. Idempotent, and safe to call
    /// concurrently with itself or with <see cref="Dispose"/>.
    /// </summary>
    /// <remarks>
    /// The whole create → publish → enable sequence runs under
    /// <c>_gate</c>. Previously the null check took the lock and then released
    /// it before constructing, which left three races on the service's spine:
    ///
    /// <list type="bullet">
    /// <item>two callers (timer tick + startup) could both pass the check and
    /// each build a watcher — one won <c>_fsw</c>, the other leaked, still
    /// raising events into the same channel and holding its inotify
    /// watches;</item>
    /// <item>events were enabled BEFORE the instance was published, so an
    /// <c>Error</c> raised in that window found <c>_fsw</c> still null,
    /// failed <c>HandleWatcherError</c>'s identity check, and returned without
    /// retiring anything — then the dead watcher was published, and every
    /// later <c>Start()</c> saw non-null and no-op'd. Event-driven ingestion
    /// stayed silently dead until the process restarted;</item>
    /// <item>a timer-driven restart could interleave with <c>Dispose</c> and
    /// resurrect a watcher after shutdown had begun.</item>
    /// </list>
    ///
    /// <para>Publishing before enabling is safe — every handler is attached
    /// before either — and it is what makes the error path able to find the
    /// instance it needs to retire.</para>
    ///
    /// <para>Holding the lock across <c>EnableRaisingEvents</c> means an event
    /// can arrive on a threadpool thread while we still hold it.
    /// <c>OnEvent</c> takes <c>_gate</c> too, so that thread simply waits out
    /// the remaining microseconds of setup; it cannot deadlock, because
    /// nothing under the gate blocks on event delivery.</para>
    /// </remarks>
    public void Start()
    {
        if (!Directory.Exists(_root))
        {
            _logger.LogWarning("MaildirWatcher: {Path} does not exist; watcher disabled.", _root);
            return;
        }

        FileSystemWatcher? fsw = null;
        try
        {
            lock (_gate)
            {
                if (_disposed || _fsw is not null) return;

                fsw = CreateWatcher(_root);
                fsw.IncludeSubdirectories = true;
                fsw.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName;
                fsw.Created  += (_, e) => OnEvent(e.FullPath);
                fsw.Deleted  += (_, e) => OnEvent(e.FullPath);
                fsw.Renamed  += (_, e) => OnEvent(e.FullPath);
                fsw.Changed  += (_, e) => OnEvent(e.FullPath);
                fsw.Error    += (sender, e) => HandleWatcherError((FileSystemWatcher)sender!, e.GetException());
                // Publish BEFORE enabling, so an Error raised the instant
                // events start flowing finds this instance in _fsw and can
                // retire it. Handlers are already attached either way, so
                // nothing is dropped.
                _fsw = fsw;
                EnableWatcher(fsw);
            }
        }
        catch (Exception ex)
        {
            // Enable failed after publication — clear it, or every later
            // Start() sees a non-null dead watcher and no-ops forever.
            lock (_gate)
            {
                if (ReferenceEquals(_fsw, fsw)) _fsw = null;
            }
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
    private bool _disposed;
    private readonly Lock _gate = new();
    private Task? _debounceTask;

    /// <summary>
    /// Test seam: is the debounce loop currently running? It nulls
    /// <c>_debounceTask</c> under the gate on the way out, so this is the
    /// loop's own termination signal rather than an inference from it.
    /// </summary>
    /// <remarks>
    /// Exists because the alternative — "assert no pulse arrives in the next
    /// N milliseconds" — is not a test of termination, it is a test of the
    /// runner's timing. A stall longer than N lets a legitimate straggler
    /// pulse land inside the silence window and fails the build for nothing,
    /// which this test did twice before the seam existed. Widening N only
    /// moves the threshold; asking the loop directly removes it.
    /// </remarks>
    internal bool DebounceLoopRunning
    {
        get { lock (_gate) { return _debounceTask is not null; } }
    }

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
        FileSystemWatcher? toDispose;
        lock (_gate)
        {
            // _disposed latches under the same gate Start() checks, so a timer
            // tick racing shutdown cannot bring a watcher back up behind us.
            _disposed = true;
            toDispose = _fsw;
            _fsw = null;
        }
        toDispose?.Dispose();
        _pulses.Writer.TryComplete();
    }
}
