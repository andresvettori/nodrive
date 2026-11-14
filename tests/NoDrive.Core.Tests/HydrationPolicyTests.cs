using NoDrive.Core.Hydration;
using Xunit;

namespace NoDrive.Core.Tests;

public class HydrationPolicyTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData(2, 0, true)]      // short wait, first attempt: wait and retry
    [InlineData(5, 1, true)]      // exactly at the budget is still fine
    [InlineData(6, 0, false)]     // longer than the budget: fail now (measured: a 120 s Retry-After errored in 0.18 s)
    [InlineData(1, 2, false)]     // retry count exhausted
    public void Throttle_decisions_honour_short_waits_and_fail_fast_on_long_ones(int retryAfterSeconds, int attempts, bool expectRetry)
    {
        var d = new HydrationPolicy().DecideThrottle(TimeSpan.FromSeconds(retryAfterSeconds), attempts);
        Assert.Equal(expectRetry, d.Retry);
    }

    [Fact]
    public void A_missing_Retry_After_is_treated_as_a_long_wait()
    {
        Assert.False(new HydrationPolicy().DecideThrottle(null, 0).Retry);
    }

    [Fact]
    public void Defaults_match_the_p0_measurements()
    {
        var p = new HydrationPolicy();
        Assert.Equal(TimeSpan.FromSeconds(15), p.NoProgressTimeout);
        Assert.True(p.NoProgressTimeout < TimeSpan.FromSeconds(60));       // always well inside the platform deadline
        Assert.Equal(TimeSpan.FromSeconds(30), p.FailureMemory);
    }

    [Fact]
    public void FailureMemory_fails_retries_inside_the_window_and_forgets_afterwards()
    {
        var clock = new ManualClock();
        var mem = new FailureMemory(TimeSpan.FromSeconds(30), clock);
        Assert.False(mem.IsRecentlyFailed("item", out _));

        mem.Record("item");
        clock.Now += TimeSpan.FromSeconds(10);
        Assert.True(mem.IsRecentlyFailed("item", out var age));
        Assert.Equal(TimeSpan.FromSeconds(10), age);
        Assert.False(mem.IsRecentlyFailed("other", out _));

        clock.Now += TimeSpan.FromSeconds(21);                              // 31 s after the failure
        Assert.False(mem.IsRecentlyFailed("item", out _));
    }

    [Fact]
    public void FailureMemory_can_be_cleared_after_a_success()
    {
        var mem = new FailureMemory(TimeSpan.FromSeconds(30), new ManualClock());
        mem.Record("item");
        mem.Clear("item");
        Assert.False(mem.IsRecentlyFailed("item", out _));
    }
}
