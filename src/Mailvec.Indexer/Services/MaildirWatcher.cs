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

    public MaildirWatcher(IOptions<ArchiveOptions> archive, IOptions<IndexerOptions> indexer, ILogger<MaildirWatcher> logger)
    {
        _root = PathExpansion.Expand(archive.Value.MaildirRoot);
        _debounce = TimeSpan.FromMilliseconds(indexer.Value.DebounceMilliseconds);
        _logger = logger;
    }

    /// <summary>
    /// Pulses fire after a quiet period — the scanner should call ScanAll() each time.
    /// Cancellation closes the watcher.
    /// </summary>
    public ChannelReader<byte> Pulses => _pulses.Reader;

    public void Start()
    {
        if (_fsw is not null) return;
        if (!Directory.Exists(_root))
        {
            _logger.LogWarning("MaildirWatcher: {Path} does not exist; watcher disabled.", _root);
            return;
        }

        _fsw = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true,
        };
        _fsw.Created  += (_, e) => OnEvent(e.FullPath);
        _fsw.Deleted  += (_, e) => OnEvent(e.FullPath);
        _fsw.Renamed  += (_, e) => OnEvent(e.FullPath);
        _fsw.Changed  += (_, e) => OnEvent(e.FullPath);
        _fsw.Error    += (_, e) => _logger.LogWarning(e.GetException(), "FileSystemWatcher reported an error");

        _logger.LogInformation("MaildirWatcher started on {Path} (debounce {Ms}ms)", _root, _debounce.TotalMilliseconds);
    }

    private DateTimeOffset _lastEventAt;
    private readonly Lock _gate = new();
    private Task? _debounceTask;

    private void OnEvent(string fullPath)
    {
        // Ignore events on tmp/ — mbsync writes there before atomic rename into new/.
        if (fullPath.Contains("/tmp/", StringComparison.Ordinal) || fullPath.EndsWith("/tmp", StringComparison.Ordinal))
            return;

        lock (_gate)
        {
            _lastEventAt = DateTimeOffset.UtcNow;
            _debounceTask ??= Task.Run(DebounceLoopAsync);
        }
    }

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
