namespace NoDrive.Core.Hydration;

/// <summary>Tunables of read-ahead (P1 finding from real playback: tiny reads cost a network round trip each).</summary>
public sealed record ReadAheadOptions
{
    /// <summary>Window on the first access to a file or after a seek.</summary>
    public long InitialBytes { get; init; } = 256L * 1024;

    /// <summary>First window after a sequential continuation (a second sequential read proves a streaming reader, not a thumbnail).</summary>
    public long SequentialStartBytes { get; init; } = 1024L * 1024;

    /// <summary>The window doubles on every sequential continuation up to this cap.</summary>
    public long MaxBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>Platform requirement: offsets and lengths of transferred data are multiples of this, except at the end of the file.</summary>
    public long Alignment { get; init; } = 4096;

    /// <summary>How many files the planner remembers (LRU).</summary>
    public int TrackedFiles { get; init; } = 256;
}

/// <summary>
/// Decides how many bytes to download for one FETCH_DATA request: the bytes the caller needs (required range) plus a read-ahead window,
/// limited to the range Windows says we may fill (optional range) and to the end of the file. Sequential access (a request starting where
/// the previous fetch ended) doubles the window; a seek resets it. Whole-file requests are returned unchanged (nothing to read ahead).
/// </summary>
public sealed class ReadAheadPlanner(ReadAheadOptions? options = null)
{
    private sealed class State { public long NextExpected; public long Window; public long LastUse; }

    private readonly ReadAheadOptions _o = options ?? new ReadAheadOptions();
    private readonly Dictionary<string, State> _files = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private long _tick;

    /// <summary>The number of bytes to fetch starting at <paramref name="requiredOffset"/> (never less than the required length).</summary>
    public long Plan(string itemId, long requiredOffset, long requiredLength, long optionalOffset, long optionalLength, long fileSize)
    {
        long requiredEnd = requiredOffset + requiredLength;
        long fetch = requiredLength;
        lock (_lock)
        {
            _files.TryGetValue(itemId, out var st);
            bool sequential = st is not null && Math.Abs(requiredOffset - st.NextExpected) <= _o.Alignment;
            long window = sequential ? Math.Min(_o.MaxBytes, Math.Max(_o.SequentialStartBytes, st!.Window * 2)) : _o.InitialBytes;

            // Only read ahead when Windows offers the continuation (optional range right after the required one) and the request is not already huge.
            bool offered = optionalLength > 0 && optionalOffset <= requiredEnd + _o.Alignment;
            if (offered && requiredLength < window)
            {
                long wanted = Math.Max(requiredLength, window);
                long limit = Math.Min(fileSize, optionalOffset + optionalLength) - requiredOffset;     // do not pass what Windows offers or the file end
                fetch = Math.Min(wanted, Math.Max(requiredLength, limit));
            }

            long end = requiredOffset + fetch;
            if (end < fileSize) { end = AlignUp(end); }                                                // aligned unless it is the file end
            end = Math.Min(end, fileSize);
            fetch = Math.Max(requiredLength, end - requiredOffset);

            st ??= new State();
            st.NextExpected = requiredOffset + fetch;
            st.Window = window;
            st.LastUse = ++_tick;
            _files[itemId] = st;
            if (_files.Count > _o.TrackedFiles) { Evict(); }
        }
        return fetch;
    }

    private long AlignUp(long value) => (value + _o.Alignment - 1) / _o.Alignment * _o.Alignment;

    private void Evict()
    {
        string oldest = "";
        long best = long.MaxValue;
        foreach (var kv in _files) { if (kv.Value.LastUse < best) { best = kv.Value.LastUse; oldest = kv.Key; } }
        _files.Remove(oldest);
    }
}
