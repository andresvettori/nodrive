namespace NoDrive.Core.Hydration;

/// <summary>Timing rules for download requests, from P0 step 7 (all measured against the 60 s platform deadline).</summary>
public sealed record HydrationPolicy
{
    /// <summary>A request that makes no progress for this long is failed (Explorer shows an error instead of waiting 60 s).</summary>
    public TimeSpan NoProgressTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>A throttling response asking for a longer wait than this fails the request immediately.</summary>
    public TimeSpan MaxRetryAfterWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How many times a short Retry-After is honoured within one request.</summary>
    public int MaxThrottleRetries { get; init; } = 2;

    /// <summary>After a no-progress/throttle/not-found failure, further requests for the same item fail instantly for this long (Windows re-issues failed requests).</summary>
    public TimeSpan FailureMemory { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Decides what to do with a 429/503: wait (and retry) or fail now.</summary>
    public ThrottleDecision DecideThrottle(TimeSpan? retryAfter, int attemptsSoFar)
    {
        var wait = retryAfter ?? TimeSpan.FromSeconds(30);
        if (wait > MaxRetryAfterWait || attemptsSoFar >= MaxThrottleRetries) { return ThrottleDecision.FailNow(wait); }
        return ThrottleDecision.WaitThenRetry(wait);
    }
}

public readonly record struct ThrottleDecision(bool Retry, TimeSpan Wait)
{
    public static ThrottleDecision WaitThenRetry(TimeSpan wait) => new(true, wait);
    public static ThrottleDecision FailNow(TimeSpan requested) => new(false, requested);
}

/// <summary>
/// Remembers items whose download just failed in a way where an immediate retry is pointless, so Windows' automatic re-request
/// fails in microseconds instead of waiting out the watchdog again. A dropped connection is NOT remembered: the retry usually succeeds.
/// </summary>
public sealed class FailureMemory(TimeSpan window, TimeProvider? clock = null)
{
    private readonly Dictionary<string, DateTimeOffset> _failed = [];
    private readonly object _lock = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public void Record(string itemId)
    {
        lock (_lock)
        {
            _failed[itemId] = _clock.GetUtcNow();
            if (_failed.Count > 4096) { Trim(); }
        }
    }

    public void Clear(string itemId) { lock (_lock) { _failed.Remove(itemId); } }

    /// <summary>True when <paramref name="itemId"/> failed less than the window ago; <paramref name="age"/> is how long ago.</summary>
    public bool IsRecentlyFailed(string itemId, out TimeSpan age)
    {
        lock (_lock)
        {
            if (_failed.TryGetValue(itemId, out var at))
            {
                age = _clock.GetUtcNow() - at;
                if (age < window) { return true; }
                _failed.Remove(itemId);
            }
            age = TimeSpan.Zero;
            return false;
        }
    }

    private void Trim()
    {
        var now = _clock.GetUtcNow();
        foreach (var key in _failed.Where(kv => now - kv.Value >= window).Select(kv => kv.Key).ToList()) { _failed.Remove(key); }
    }
}
