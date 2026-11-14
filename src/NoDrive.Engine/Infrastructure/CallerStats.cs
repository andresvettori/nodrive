using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace NoDrive.Engine.Infrastructure;

/// <summary>
/// Local-only record of how each calling process reads placeholders (research/app-compatibility.md, plan E3d): which apps ask for small
/// ranges and which make Windows request whole files. Never leaves the machine.
/// </summary>
internal sealed class CallerStats
{
    private sealed class Stat { public long Requests; public long Bytes; public long WholeFile; public long Largest; }
    private readonly ConcurrentDictionary<string, Stat> _stats = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string? imagePath, long requestedBytes, long fileSize)
    {
        string name = string.IsNullOrEmpty(imagePath) ? "(unknown)" : Path.GetFileName(imagePath);
        var s = _stats.GetOrAdd(name, _ => new Stat());
        Interlocked.Increment(ref s.Requests);
        Interlocked.Add(ref s.Bytes, requestedBytes);
        if (fileSize > 4 * 1024 * 1024 && requestedBytes >= fileSize * 9 / 10) { Interlocked.Increment(ref s.WholeFile); }
        long cur;
        do { cur = Interlocked.Read(ref s.Largest); } while (requestedBytes > cur && Interlocked.CompareExchange(ref s.Largest, requestedBytes, cur) != cur);
    }

    public string Summary()
    {
        if (_stats.IsEmpty) { return "no download requests yet"; }
        var sb = new StringBuilder();
        foreach (var kv in _stats.OrderByDescending(k => k.Value.Bytes))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{kv.Key}: {kv.Value.Requests} requests, {kv.Value.Bytes / 1048576.0:F1} MiB requested, {kv.Value.WholeFile} whole-file, largest {kv.Value.Largest / 1048576.0:F1} MiB; ");
        }
        return sb.ToString().TrimEnd(' ', ';');
    }
}
