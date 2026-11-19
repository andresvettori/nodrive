using System.Diagnostics;

namespace NoDrive.Core.Net;

/// <summary>
/// Shared rate limiter for one direction (download or upload). Virtual-time scheduling: every request reserves the next slot on the pipe,
/// so concurrent transfers share the limit fairly and a small burst (a quarter second) is allowed. The limit can change at any time.
/// Threading: safe from any thread. Never blocks a thread: callers await <see cref="WaitAsync"/>.
/// </summary>
public sealed class BandwidthLimiter
{
    private static readonly TimeSpan Burst = TimeSpan.FromMilliseconds(250);
    private readonly object _gate = new();
    private long _bytesPerSecond;
    private long _nextFreeTicks;

    /// <summary>Limit in bytes per second; zero or negative means unlimited.</summary>
    public long BytesPerSecond
    {
        get => Volatile.Read(ref _bytesPerSecond);
        set => Volatile.Write(ref _bytesPerSecond, Math.Max(0, value));
    }

    /// <summary>Reserves <paramref name="bytes"/> at time <paramref name="nowTicks"/> (TimeSpan ticks) and returns how long the caller must wait.</summary>
    public TimeSpan Reserve(long bytes, long nowTicks)
    {
        long rate = BytesPerSecond;
        if (rate <= 0 || bytes <= 0) { return TimeSpan.Zero; }
        long cost = (long)((double)bytes / rate * TimeSpan.TicksPerSecond);
        lock (_gate)
        {
            long start = Math.Max(_nextFreeTicks, nowTicks - Burst.Ticks);
            _nextFreeTicks = start + cost;
            return start > nowTicks ? TimeSpan.FromTicks(start - nowTicks) : TimeSpan.Zero;
        }
    }

    private static readonly long Origin = Stopwatch.GetTimestamp();

    /// <summary>
    /// Monotonic time in TimeSpan ticks since the first use. NOT `GetTimestamp() * TicksPerSecond / Frequency`: the raw timestamp counts from boot and that product overflows a
    /// long after about 26 hours of uptime, which turned every wait into a practically infinite one (found on the test PC, 2026-11-19).
    /// </summary>
    internal static long NowTicks() => ToTicks(Stopwatch.GetTimestamp() - Origin, Stopwatch.Frequency);

    /// <summary>Converts a Stopwatch timestamp to TimeSpan ticks without overflowing a long (decimal arithmetic).</summary>
    public static long ToTicks(long timestamp, long frequency) => (long)((decimal)timestamp * TimeSpan.TicksPerSecond / frequency);

    /// <summary>Waits until <paramref name="bytes"/> may be transferred.</summary>
    public ValueTask WaitAsync(long bytes, CancellationToken ct)
    {
        var wait = Reserve(bytes, NowTicks());
        return wait <= TimeSpan.Zero ? ValueTask.CompletedTask : new ValueTask(Task.Delay(wait, ct));
    }
}
