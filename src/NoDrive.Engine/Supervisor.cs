using System.Diagnostics;
using NoDrive.Engine.CloudFiles;
using NoDrive.Engine.Infrastructure;
using NoDrive.Engine.Shell;
using NoDrive.Engine.Switching;

namespace NoDrive.Engine;

/// <summary>
/// Self-protection (design rule 9). Every few seconds: fail requests that have been pending far longer than any healthy transfer (so Explorer never waits for the platform's 60 s deadline),
/// release memory when the process is larger than it should be, and restart the engine cleanly if memory keeps growing (a leak must never eat the PC).
/// </summary>
internal static class Supervisor
{
    private static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(50);
    private const long SoftLimitMb = 400, HardLimitMb = 1200;
    private static int s_overHard;
    private static DateTime s_lastCollect;

    public static void Start(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    CloudProvider.SweepStuck(StuckAfter);
                    CheckMemory();
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log.Warn($"supervisor: {ex.Message}"); }
            }
        }, CancellationToken.None);
    }

    private static void CheckMemory()
    {
        using var p = Process.GetCurrentProcess();
        long mb = p.PrivateMemorySize64 / 1048576;
        if (mb > SoftLimitMb && DateTime.UtcNow - s_lastCollect > TimeSpan.FromMinutes(5))
        {
            s_lastCollect = DateTime.UtcNow;
            Log.Warn($"supervisor: private memory is {mb} MB (soft limit {SoftLimitMb}); collecting");
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        if (mb > HardLimitMb) { s_overHard++; } else { s_overHard = 0; }
        if (s_overHard >= 3)
        {
            Log.Error($"supervisor: private memory {mb} MB stayed above {HardLimitMb} MB; restarting the engine");
            ActivityLog.Add("error", "NoDrive", "failed", detail: $"memory use reached {mb} MB; NoDrive restarted itself");
            SwitchAssistant.RestartEngine();
            TrayWindow.RequestQuit();
            s_overHard = 0;
        }
    }
}
