namespace NoDrive.Graph;

/// <summary>
/// One gate for ALL Graph traffic: when any request is throttled (429/503 with Retry-After) every other request waits for the same
/// pause instead of hammering the service in parallel (Microsoft's guidance: honour Retry-After for the whole app).
/// </summary>
public sealed class ThrottleGate(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _pausedUntilTicks;

    /// <summary>Extends the pause to at least now + <paramref name="wait"/> (never shortens an existing longer pause).</summary>
    public void PauseFor(TimeSpan wait)
    {
        long until = _clock.GetUtcNow().Ticks + wait.Ticks;
        long current;
        do { current = Interlocked.Read(ref _pausedUntilTicks); if (until <= current) { return; } }
        while (Interlocked.CompareExchange(ref _pausedUntilTicks, until, current) != current);
    }

    /// <summary>Clears any pause (the PC woke up or the network came back: a Retry-After from before is stale).</summary>
    public void Reset() => Interlocked.Exchange(ref _pausedUntilTicks, 0);

    /// <summary>The remaining pause, or zero.</summary>
    public TimeSpan Remaining
    {
        get
        {
            long left = Interlocked.Read(ref _pausedUntilTicks) - _clock.GetUtcNow().Ticks;
            return left > 0 ? TimeSpan.FromTicks(left) : TimeSpan.Zero;
        }
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        TimeSpan left;
        while ((left = Remaining) > TimeSpan.Zero) { await Task.Delay(left, _clock, ct).ConfigureAwait(false); }
    }
}
