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
        _debounce = TimeSpan.FromMilliseconds(indexer.Value.DebounceMilliseconds);
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
        if (_fsw is not null) return;
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
            fsw.Error    += (_, e) =>
            {
                // A buffer overflow means events were dropped — we don't know which
                // files changed. Force a pulse so the scanner does a full pass
                // instead of waiting up to a full timer interval for the next one.
                _logger.LogWarning(e.GetException(), "FileSystemWatcher reported an error; forcing a rescan");
                _pulses.Writer.TryWrite(0);
            };
            // Enable LAST, with every handler already attached, so an event
            // landing in the setup window can't be silently dropped.
            fsw.EnableRaisingEvents = true;
            _fsw = fsw;
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
                lock (_gate) { _debounceTask = null; }
                return;
            }
        }
    }

    public void Dispose()
    {
        _fsw?.Dispose();
        _pulses.Writer.TryComplete();
    }
}
