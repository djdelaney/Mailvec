namespace Mailvec.Parse;

/// <summary>
/// Admission control: at most <c>ParseHostOptions.MaxConcurrentParses</c>
/// parses run at once. A request waits for a slot up to
/// <c>ParseHostOptions.SlotWaitSeconds</c> and is answered 503 (<c>busy</c>) if none frees — the caller classifies
/// that as <c>Unavailable</c>, waits, and retries, which is the right shape
/// for "the service is full", not a strike. The slot is released when the
/// parse itself finishes, even after its request has been abandoned.
/// </summary>
public sealed class ParseGate(int slots)
{
    private readonly SemaphoreSlim _slots = new(slots, slots);

    public int Slots { get; } = slots;

    public Task<bool> TryEnterAsync(TimeSpan wait, CancellationToken ct) => _slots.WaitAsync(wait, ct);

    public void Exit() => _slots.Release();
}
