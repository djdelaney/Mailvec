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
        migrator.EnsureUpToDate();

        logger.LogInformation("Initial Maildir scan starting");
        scanner.ScanAll(stoppingToken);

        watcher.Start();

        var rescanInterval = TimeSpan.FromSeconds(Math.Max(1, indexerOptions.Value.ScanIntervalSeconds));
        using var rescanTimer = new PeriodicTimer(rescanInterval);

        var pulseTask = ReadPulsesAsync(stoppingToken);
        var timerTask = ReadTimerAsync(rescanTimer, stoppingToken);

        try
        {
            await Task.WhenAll(pulseTask, timerTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("MessageIngestService stopping");
        }
    }

    private async Task ReadPulsesAsync(CancellationToken ct)
    {
        await foreach (var _ in watcher.Pulses.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                scanner.ScanAll(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Watcher-triggered scan failed");
            }
        }
    }

    private async Task ReadTimerAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    scanner.ScanAll(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Timer-triggered scan failed");
                }
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
