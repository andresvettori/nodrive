using NoDrive.Core.Net;
using Xunit;

namespace NoDrive.Core.Tests;

public class BandwidthLimiterTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    [Fact]
    public void Unlimited_never_waits()
    {
        var l = new BandwidthLimiter();
        Assert.Equal(TimeSpan.Zero, l.Reserve(1_000_000_000, 0));
    }

    [Fact]
    public void Requests_queue_behind_each_other_at_the_configured_rate()
    {
        var l = new BandwidthLimiter { BytesPerSecond = 1000 };
        long now = 10 * Second;
        Assert.Equal(TimeSpan.Zero, l.Reserve(1000, now));                     // the first request goes straight out (quarter-second burst credit)
        Assert.InRange(l.Reserve(1000, now).TotalSeconds, 0.74, 0.76);          // the second waits for the first one's transfer time, less the credit
        Assert.InRange(l.Reserve(500, now).TotalSeconds, 1.74, 1.76);           // the third waits behind both
    }

    [Fact]
    public void An_idle_pipe_does_not_bank_unlimited_credit()
    {
        var l = new BandwidthLimiter { BytesPerSecond = 1000 };
        l.Reserve(1000, 0);
        long later = 100 * Second;                                              // long idle
        Assert.Equal(TimeSpan.Zero, l.Reserve(250, later));                     // at most the quarter-second burst is free
        Assert.Equal(TimeSpan.Zero, l.Reserve(5000, later));                    // this one starts now but occupies the pipe for 5 s
        Assert.InRange(l.Reserve(100, later).TotalSeconds, 4.99, 5.01);
    }

    [Fact]
    public void Lowering_the_limit_applies_to_new_requests()
    {
        var l = new BandwidthLimiter { BytesPerSecond = 1_000_000 };
        l.Reserve(1000, 0);
        l.BytesPerSecond = 100;
        l.Reserve(1000, 0);                                                     // 10 s at the new rate
        Assert.True(l.Reserve(1, 0) >= TimeSpan.FromSeconds(9));
    }
}
