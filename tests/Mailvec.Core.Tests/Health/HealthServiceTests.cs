using Mailvec.Core.Data;
using Mailvec.Core.Health;

namespace Mailvec.Core.Tests.Health;

public class HealthServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 2, 18, 50, 0, TimeSpan.Zero);

    [Fact]
    public void Not_stuck_when_backlog_drained_even_with_stale_failure()
    {
        // Fully embedded; a leftover failure on record is just idle, not broken.
        HealthService.IsStuck(
            backlog: 0,
            consecutiveFailures: 1,
            lastSuccessAt: Now.AddHours(-2),
            lastFailureAt: Now.AddMinutes(-30),
            now: Now).ShouldBeFalse();
    }

    [Fact]
    public void Stuck_when_consecutive_failures_reach_threshold()
    {
        HealthService.IsStuck(
            backlog: 5,
            consecutiveFailures: EmbedderHealthKeys.StuckThreshold,
            lastSuccessAt: Now.AddMinutes(-1),
            lastFailureAt: Now,
            now: Now).ShouldBeTrue();
    }

    [Fact]
    public void Stuck_via_time_backstop_before_counter_trips()
    {
        // The slow-failing case: only 1 failure recorded (each cycle burns
        // minutes of Ollama timeout) but no success in over 10 minutes while a
        // backlog remains. Counter alone wouldn't flag it yet.
        HealthService.IsStuck(
            backlog: 3,
            consecutiveFailures: 1,
            lastSuccessAt: Now.AddMinutes(-40),
            lastFailureAt: Now.AddMinutes(-2),
            now: Now).ShouldBeTrue();
    }

    [Fact]
    public void Not_stuck_when_recent_success_despite_backlog()
    {
        // Actively draining: a success within the window means progress.
        HealthService.IsStuck(
            backlog: 100,
            consecutiveFailures: 1,
            lastSuccessAt: Now.AddMinutes(-1),
            lastFailureAt: Now.AddMinutes(-5),
            now: Now).ShouldBeFalse();
    }

    [Fact]
    public void Not_stuck_when_failure_is_recent_but_success_also_recent()
    {
        // last attempt succeeded (success newer than failure) → healthy.
        HealthService.IsStuck(
            backlog: 10,
            consecutiveFailures: 0,
            lastSuccessAt: Now.AddMinutes(-15),
            lastFailureAt: Now.AddMinutes(-20),
            now: Now).ShouldBeFalse();
    }

    [Fact]
    public void Stuck_when_never_succeeded_and_failing_with_backlog()
    {
        // Fresh DB that has never managed a successful embed, has a backlog,
        // and the last attempt failed longer ago than the stale window.
        HealthService.IsStuck(
            backlog: 50,
            consecutiveFailures: 1,
            lastSuccessAt: null,
            lastFailureAt: Now.AddMinutes(-11),
            now: Now).ShouldBeTrue();
    }

    [Fact]
    public void Not_stuck_with_no_signal_yet()
    {
        // Brand-new system: backlog exists but no attempt has run.
        HealthService.IsStuck(
            backlog: 50,
            consecutiveFailures: 0,
            lastSuccessAt: null,
            lastFailureAt: null,
            now: Now).ShouldBeFalse();
    }
}
