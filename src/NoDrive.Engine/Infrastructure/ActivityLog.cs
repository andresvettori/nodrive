using NoDrive.Core.Net;

namespace NoDrive.Engine.Infrastructure;

/// <summary>One finished operation shown in the activity screen.</summary>
internal sealed record ActivityEntry(long Id, DateTime UtcTime, string Kind, string Name, string Outcome, long Bytes, long Ms, string? Detail);

/// <summary>A transfer in progress. Progress is written by the transfer thread and read by the web UI.</summary>
internal sealed class ActiveTransfer(long id, string kind, string name, long total)
{
    private long _done;
    public long Id { get; } = id;
    public string Kind { get; } = kind;
    public string Name { get; } = name;
    public long Total { get; } = total;
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public long Done { get => Interlocked.Read(ref _done); set => Interlocked.Exchange(ref _done, value); }
}

/// <summary>
/// Bounded in-memory journal of what the engine is doing (kept small on purpose: 400 entries, 32 transfers) plus the shared bandwidth limiters
/// and byte counters. Thread-safe; every method is cheap and never blocks on I/O.
/// Kinds: download, upload, create, update, rename, delete, sync, error. Outcomes: ok, failed, cancelled, skipped.
/// </summary>
internal static class ActivityLog
{
    private const int MaxEntries = 400, MaxActive = 32;
    private static readonly object Gate = new();
    private static readonly LinkedList<ActivityEntry> Entries = new();
    private static readonly Dictionary<long, ActiveTransfer> Active = [];
    private static long s_nextId, s_bytesDown, s_bytesUp;

    public static readonly BandwidthLimiter Download = new();
    public static readonly BandwidthLimiter Upload = new();

    public static long BytesDown => Interlocked.Read(ref s_bytesDown);
    public static long BytesUp => Interlocked.Read(ref s_bytesUp);
    public static void AddDown(long bytes) => Interlocked.Add(ref s_bytesDown, bytes);
    public static void AddUp(long bytes) => Interlocked.Add(ref s_bytesUp, bytes);

    public static void Add(string kind, string name, string outcome = "ok", long bytes = 0, long ms = 0, string? detail = null)
    {
        lock (Gate)
        {
            Entries.AddLast(new ActivityEntry(++s_nextId, DateTime.UtcNow, kind, name, outcome, bytes, ms, detail));
            while (Entries.Count > MaxEntries) { Entries.RemoveFirst(); }
        }
    }

    public static ActiveTransfer Begin(string kind, string name, long total)
    {
        var t = new ActiveTransfer(Interlocked.Increment(ref s_nextId), kind, name, total);
        lock (Gate) { if (Active.Count < MaxActive) { Active[t.Id] = t; } }
        return t;
    }

    public static void End(ActiveTransfer t, string outcome, string? detail = null)
    {
        lock (Gate) { Active.Remove(t.Id); }
        Add(t.Kind, t.Name, outcome, t.Done, (long)(DateTime.UtcNow - t.StartedUtc).TotalMilliseconds, detail);
    }

    /// <summary>Entries newer than <paramref name="afterId"/>, oldest first, plus the transfers in progress.</summary>
    public static (List<ActivityEntry> Recent, List<ActiveTransfer> Active) Snapshot(long afterId)
    {
        lock (Gate) { return ([.. Entries.Where(e => e.Id > afterId)], [.. Active.Values]); }
    }

    public static void ApplyLimits(EngineConfig c)
    {
        Download.BytesPerSecond = Math.Max(0, c.DownloadKiBps) * 1024L;
        Upload.BytesPerSecond = Math.Max(0, c.UploadKiBps) * 1024L;
    }
}
