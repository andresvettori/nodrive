using System.Diagnostics;
using System.Globalization;

namespace NoDrive.Spikes.Shared;

internal static class ProcessStats
{
    private static readonly Stopwatch s_since = Stopwatch.StartNew();

    /// <summary>Prints the GC settings the runtime actually applied (proves RuntimeHostConfigurationOption reached the AOT binary).</summary>
    public static void PrintGcConfiguration()
    {
        Console.WriteLine($"gc: server={System.Runtime.GCSettings.IsServerGC} latency={System.Runtime.GCSettings.LatencyMode}");
        foreach (var kv in GC.GetConfigurationVariables().OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"gc: {kv.Key}={kv.Value}");
        }
    }

    public static string Sample(string tag = "")
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        var gc = GC.GetGCMemoryInfo();
        return string.Create(CultureInfo.InvariantCulture,
            $"[{s_since.Elapsed:hh\\:mm\\:ss}] {tag,-10} private={p.PrivateMemorySize64 / 1048576.0,7:F1} MB  ws={p.WorkingSet64 / 1048576.0,7:F1} MB  gcCommitted={gc.TotalCommittedBytes / 1048576.0,6:F1} MB  gcHeap={GC.GetTotalMemory(false) / 1048576.0,6:F1} MB  threads={p.Threads.Count,3}  handles={p.HandleCount,4}");
    }

    /// <summary>Prints a sample every <paramref name="interval"/> on a background thread until the process exits.</summary>
    public static void StartSampler(TimeSpan interval, string tag = "idle")
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(interval);
                Console.WriteLine(Sample(tag));
            }
        })
        { IsBackground = true, Name = "stats-sampler" };
        t.Start();
    }

    /// <summary>The post-burst sequence the engine will use: aggressive compacting full GC, twice, five seconds apart.</summary>
    public static async Task ReleaseAfterBurstAsync()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive);
        Console.WriteLine(Sample("after-gc1"));
        await Task.Delay(TimeSpan.FromSeconds(5));
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive);
        Console.WriteLine(Sample("after-gc2"));
    }
}
