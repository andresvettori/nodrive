using NoDrive.Core.Net;
using Xunit;

namespace NoDrive.Core.Tests;

public class AdjustableGateTests
{
    [Fact]
    public async Task Limits_concurrency_and_serves_waiters_in_order()
    {
        var gate = new AdjustableGate(2);
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        var third = gate.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(third.IsCompleted);
        gate.Release();
        await third.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(2, gate.Running);
    }

    [Fact]
    public async Task Raising_the_limit_releases_waiting_work_immediately()
    {
        var gate = new AdjustableGate(1);
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        var a = gate.WaitAsync(TestContext.Current.CancellationToken);
        var b = gate.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(a.IsCompleted);
        gate.Limit = 3;
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(3, gate.Running);
    }

    [Fact]
    public async Task Lowering_the_limit_lets_running_work_finish_and_holds_back_new_work()
    {
        var gate = new AdjustableGate(4);
        for (int i = 0; i < 4; i++) { await gate.WaitAsync(TestContext.Current.CancellationToken); }
        gate.Limit = 2;
        var next = gate.WaitAsync(TestContext.Current.CancellationToken);
        gate.Release();                       // 3 running, limit 2: still held back
        gate.Release();                       // 2 running: still held back (not below the limit)
        Assert.False(next.IsCompleted);
        gate.Release();                       // 1 running: now there is room
        await next.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_cancelled_waiter_never_takes_a_slot()
    {
        var gate = new AdjustableGate(1);
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        var cancelled = gate.WaitAsync(cts.Token);
        var live = gate.WaitAsync(TestContext.Current.CancellationToken);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        gate.Release();
        await live.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(1, gate.Running);
    }
}
