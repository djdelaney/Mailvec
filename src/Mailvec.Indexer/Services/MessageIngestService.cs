using System.Threading.Channels;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mailvec.Indexer.Services;

/// <summary>
/// Top-level worker. On startup: applies any pending schema migrations, then
/// runs an initial full scan. After that: scans on every watcher pulse, and
/// also on a recurring timer as a safety net (in case events were missed).
///
/// Scans are serialized. The watcher pulse and the periodic timer are both
/// producers into a coalescing single-slot channel; one consumer runs
/// <see cref="MaildirScanner.ScanAll"/>. Two scans must never overlap: each
/// scan stamps every seen file's <c>sync_state.last_seen_at</c> to its own
/// start time (last-writer-wins) and reconciles deletions against that same
/// start time, so a slower older scan overwriting a file's timestamp after a
/// newer scan already recorded it makes the newer scan's reconciliation treat
/// the live file as stale and soft-delete it. Serializing also removes the
/// doubled attachment-extraction cost and the write-lock contention two
/// concurrent scans would otherwise create. The bounded/drop-write channel
/// coalesces a burst of triggers arriving mid-scan into exactly one follow-up
/// scan.
/// </summary>
public sealed class MessageIngestService(
    SchemaMigrator migrator,
    MaildirScanner scanner,
    MaildirWatcher watcher,
    IOptions<IndexerOptions> indexerOptions,
    ILogger<MessageIngestService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hop off the startup thread before the migration + initial scan:
        // everything before the first await runs synchronously inside
        // Host.StartAsync, so a first-ever index of a large corpus would
        // otherwise block host startup for its whole duration. (SIGTERM
        // still works either way — the host links ApplicationStopping into
        // stoppingToken — but startup shouldn't be wedged behind a scan.)
        await Task.Yield();

        migrator.EnsureUpToDate();

        logger.LogInformation("Initial Maildir scan starting");
        try
        {
            scanner.ScanAll(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same guard RunScansAsync gives every later scan. Without it a
            // deterministic failure here (DB locked, disk error) stops the
            // host, and launchd KeepAlive restarts it straight into the same
            // failure — a crash loop that never indexes anything. Logging and
            // carrying on lets the watcher + periodic timer retry instead.
            logger.LogError(ex, "Initial scan failed; will retry on the next watcher pulse or timer tick.");
        }

        watcher.Start();

        var rescanInterval = TimeSpan.FromSeconds(Math.Max(1, indexerOptions.Value.ScanIntervalSeconds));
        using var rescanTimer = new PeriodicTimer(rescanInterval);

        // Single-slot, drop-write: at most one scan runs and at most one is
        // queued behind it. Extra triggers that arrive while a scan is in
        // flight collapse into that single queued run (the next scan sees the
        // whole filesystem regardless of how many pulses fired).
        var scanRequests = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

        var scanTask = RunScansAsync(scanRequests.Reader, stoppingToken);
        var pulseTask = ReadPulsesAsync(scanRequests.Writer, stoppingToken);
        var timerTask = ReadTimerAsync(rescanTimer, scanRequests.Writer, stoppingToken);

        try
        {
            // The consumer joins via WhenAny, not WhenAll: it only exits when
            // the channel completes (the finally below), so putting it in the
            // WhenAll would deadlock — but leaving it entirely unobserved
            // until shutdown means a faulted consumer produces the worst kind
            // of failure: a process that looks alive, keeps accepting
            // triggers into a full channel, and never scans again. Today the
            // consumer catches everything except cancellation, so this is a
            // backstop for future throw paths — if it ever completes while
            // the producers are alive, it faulted; await it to rethrow and
            // stop the host loudly (launchd/Docker restart beats a silent
            // never-scans-again).
            var producers = Task.WhenAll(pulseTask, timerTask);
            var finished = await Task.WhenAny(producers, scanTask).ConfigureAwait(false);
            if (ReferenceEquals(finished, scanTask))
            {
                await scanTask.ConfigureAwait(false);
            }
            await producers.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // expected on shutdown
        }
        finally
        {
            // Let the consumer drain and exit once no more triggers can arrive.
            scanRequests.Writer.TryComplete();
        }

        try
        {
            await scanTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // expected on shutdown
        }

        logger.LogInformation("MessageIngestService stopping");
    }

    private async Task RunScansAsync(ChannelReader<byte> requests, CancellationToken ct)
    {
        await foreach (var _ in requests.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                scanner.ScanAll(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scan failed");
            }
        }
    }

    private async Task ReadPulsesAsync(ChannelWriter<byte> requests, CancellationToken ct)
    {
        await foreach (var _ in watcher.Pulses.ReadAllAsync(ct).ConfigureAwait(false))
        {
            // Coalesced by the bounded channel — a dropped write just means a
            // scan is already pending, which will cover this change too.
            requests.TryWrite(0);
        }
    }

    private async Task ReadTimerAsync(PeriodicTimer timer, ChannelWriter<byte> requests, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                // Idempotent no-op once running. Covers the fresh-install
                // ordering where the services start before mbsync's first
                // sync creates the Maildir root: Start() at startup logged
                // "watcher disabled" and previously that was permanent —
                // indexing then relied solely on this timer until a process
                // restart. Retrying here brings event-driven scans online
                // within one timer tick of the root appearing.
                watcher.Start();
                requests.TryWrite(0);
            }
        }
        catch (OperationCanceledException) { }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        watcher.Dispose();
        return base.StopAsync(cancellationToken);
    }
}
