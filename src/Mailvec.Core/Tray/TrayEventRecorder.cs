using Mailvec.Core.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mailvec.Core.Tray;

/// <summary>
/// Maintains the in-memory ring buffer the tray dashboard reads via
/// <c>/tray/status</c>. Runs only inside the MCP server process — the
/// indexer / embedder are separate processes and can't share heap, so instead
/// of cross-process IPC we sample the same SQLite database the workers write
/// to. Every <see cref="SampleIntervalSeconds"/> seconds we re-read counts of
/// indexed + embedded messages and append the deltas into 30 minute-buckets.
///
/// Cold-start behaviour: the first 30 samples after process start produce
/// zero-delta buckets, which is the honest answer ("we don't know yet").
/// The buckets refill over the next 30 sample intervals.
/// </summary>
public sealed class TrayEventRecorder(
    ConnectionFactory connections,
    ILogger<TrayEventRecorder> logger)
    : BackgroundService
{
    private const int BucketCount = 30;
    private const int SampleIntervalSeconds = 60;

    private readonly int[] _embeddedDeltas = new int[BucketCount];
    private readonly Lock _lock = new();
    private long _lastEmbeddedCount = -1;
    private long _lastIndexedCount = -1;
    private DateTimeOffset _lastSampleAt = DateTimeOffset.MinValue;

    public IReadOnlyList<int> SnapshotSparkline()
    {
        lock (_lock)
        {
            return [.. _embeddedDeltas];
        }
    }

    /// <summary>
    /// Embedding rate over the most recent <paramref name="windowMinutes"/>
    /// buckets, in messages-per-minute. Returns 0 if we haven't accumulated
    /// enough samples yet.
    /// </summary>
    public int CurrentRatePerMinute(int windowMinutes = 5)
    {
        var window = Math.Clamp(windowMinutes, 1, BucketCount);
        lock (_lock)
        {
            if (_lastEmbeddedCount < 0) return 0;
            int sum = 0;
            for (int i = BucketCount - window; i < BucketCount; i++)
            {
                sum += _embeddedDeltas[i];
            }
            return (int)Math.Round(sum / (double)window);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "TrayEventRecorder starting (sample every {Interval}s, {Buckets} buckets)",
            SampleIntervalSeconds, BucketCount);

        var interval = TimeSpan.FromSeconds(SampleIntervalSeconds);
        // Take an immediate baseline sample so the first delta a minute later
        // is honest (otherwise it'd report all messages embedded "in the last
        // minute" the moment the recorder starts).
        await SampleAsync().ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                await SampleAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Tray sampling iteration failed");
            }
        }
    }

    private async Task SampleAsync()
    {
        // Cheap COUNTs against indexed columns; sub-100ms on multi-100k archives.
        long embedded, indexed;
        try
        {
            await using var conn = connections.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                  (SELECT COUNT(*) FROM messages WHERE embedded_at IS NOT NULL AND deleted_at IS NULL),
                  (SELECT COUNT(*) FROM messages WHERE deleted_at IS NULL)
                """;
            using var reader = cmd.ExecuteReader();
            reader.Read();
            embedded = reader.GetInt64(0);
            indexed = reader.GetInt64(1);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tray DB sample failed; skipping bucket");
            return;
        }

        lock (_lock)
        {
            if (_lastEmbeddedCount >= 0)
            {
                var delta = (int)Math.Max(0, embedded - _lastEmbeddedCount);
                // Shift left, append newest at end. 30 entries → ~30 minutes
                // history at the default sample interval. Small enough that
                // the per-sample allocation is irrelevant.
                Array.Copy(_embeddedDeltas, 1, _embeddedDeltas, 0, BucketCount - 1);
                _embeddedDeltas[BucketCount - 1] = delta;
            }
            _lastEmbeddedCount = embedded;
            _lastIndexedCount = indexed;
            _lastSampleAt = DateTimeOffset.UtcNow;
        }
    }
}
