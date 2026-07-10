using Mailvec.Mcp.Tray;

namespace Mailvec.Mcp.Tests;

/// <summary>
/// The /tray/warm single-flight gate. The tray fires /warm on every
/// search-pane open; without the gate, rapid open/close/open stacked
/// concurrent cold KNN scans over the vector table — contending with each
/// other and with the user's first real search, the thing the warm exists
/// to speed up.
/// </summary>
public class TrayWarmGateTests : IDisposable
{
    // The gate is process-global; leave it released for other tests.
    public void Dispose() => TrayEndpoints.EndWarm();

    [Fact]
    public void Second_warm_is_refused_while_the_first_is_in_flight()
    {
        TrayEndpoints.TryBeginWarm().ShouldBeTrue();
        TrayEndpoints.TryBeginWarm().ShouldBeFalse();

        TrayEndpoints.EndWarm();
        TrayEndpoints.TryBeginWarm().ShouldBeTrue();
    }

    [Fact]
    public async Task Gate_admits_exactly_one_of_many_concurrent_warms()
    {
        var acquired = 0;
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            if (TrayEndpoints.TryBeginWarm()) Interlocked.Increment(ref acquired);
        })));

        acquired.ShouldBe(1);
    }
}
