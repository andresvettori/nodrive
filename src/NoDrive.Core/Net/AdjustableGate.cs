namespace NoDrive.Core.Net;

/// <summary>
/// An async gate like a semaphore whose limit can change while it is in use (the "bulk download speed" setting applies immediately). Raising the limit lets waiting work start at once;
/// lowering it lets running work finish and only holds back new work. Waiters are served in order; a cancelled waiter never takes a slot. Threading: safe from any thread.
/// </summary>
public sealed class AdjustableGate(int limit)
{
    private readonly object _lock = new();
    private readonly Queue<TaskCompletionSource> _waiters = new();
    private int _limit = Math.Max(1, limit);
    private int _running;

    public int Limit
    {
        get { lock (_lock) { return _limit; } }
        set { lock (_lock) { _limit = Math.Max(1, value); Pump(); } }
    }

    public int Running { get { lock (_lock) { return _running; } } }

    public Task WaitAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_running < _limit && _waiters.Count == 0) { _running++; return Task.CompletedTask; }
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(tcs);
            if (ct.CanBeCanceled) { ct.Register(() => tcs.TrySetCanceled(ct)); }
            return tcs.Task;
        }
    }

    public void Release()
    {
        lock (_lock) { _running--; Pump(); }
    }

    private void Pump()
    {
        while (_running < _limit && _waiters.Count > 0)
        {
            if (_waiters.Dequeue().TrySetResult()) { _running++; }
        }
    }
}
