namespace Mailvec.Parse;

/// <summary>
/// The two reasons this host exits on purpose, and the one mechanism for both:
/// a request that overran <see cref="ParseHostOptions.RequestTimeout"/> (the
/// parse thread can't be stopped, only abandoned — so the process is), and
/// <see cref="ParseHostOptions.MaxRequestsBeforeExit"/> served (bounding how
/// long a compromised process persists). Either way the response in flight is
/// sent first; the stop is registered on its completion. Compose's
/// <c>restart: unless-stopped</c> brings the container back in seconds.
/// </summary>
public sealed class RequestBudget(
    ParseHostOptions options,
    IHostApplicationLifetime lifetime,
    ILogger<RequestBudget> logger)
{
    private long _completed;
    private int _stopping;

    /// <summary>Requests answered by this process so far (successes and classified failures alike).</summary>
    public long Completed => Interlocked.Read(ref _completed);

    /// <summary>True once an exit has been scheduled.</summary>
    public bool Stopping => Volatile.Read(ref _stopping) == 1;

    /// <summary>Count one answered request; schedule the exit if the budget is spent.</summary>
    public void RequestCompleted(HttpContext context)
    {
        var n = Interlocked.Increment(ref _completed);
        if (options.MaxRequestsBeforeExit > 0 && n >= options.MaxRequestsBeforeExit)
            StopAfterResponse(context, $"served {n} requests (Parser:MaxRequestsBeforeExit={options.MaxRequestsBeforeExit})");
    }

    /// <summary>Exit once <paramref name="context"/>'s response has been sent.</summary>
    public void StopAfterResponse(HttpContext context, string reason)
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 1) return;
        logger.LogInformation("parse: exiting after this response — {Reason}. The container restarts.", reason);
        context.Response.OnCompleted(() =>
        {
            lifetime.StopApplication();
            return Task.CompletedTask;
        });
    }
}
