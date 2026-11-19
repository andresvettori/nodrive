using System.Diagnostics;
using NoDrive.Engine.CloudFiles;
using NoDrive.Engine.Infrastructure;

namespace NoDrive.Engine;

/// <summary>
/// Install, uninstall and recovery helpers used by the installer and by Program.
///   --quit [--wait N]                  ask a running engine to exit cleanly (exit 0: not running or stopped, 1: still running)
///   --pending                          exit 0 when nothing is waiting to sync, 3 when uploads or deletes/renames are still queued
///   --uninstall [--force] [--delete-data] [--delete-root]
///                                      quit, refuse (exit 3) while changes are unsynced unless --force, unregister the sync root,
///                                      remove autostart and shell leftovers; downloaded files stay on disk
/// Exit codes: 0 ok, 1 could not stop the engine, 3 unsynced changes.
/// </summary>
internal static class Lifecycle
{
    // Per-user names in the Global namespace: they work across Windows sessions (installer, scheduled task, terminal) and never collide between users.
    private static readonly string Sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "user";
    public static readonly string QuitEventName = $@"Global\NoDrive.Engine.Quit.{Sid}";
    public static readonly string MutexName = $@"Global\NoDrive.Engine.{Sid}";

    /// <summary>Called by the running engine: waits for <c>--quit</c> from another process and then closes the tray loop.</summary>
    public static void ListenForQuit(Action quit, CancellationToken ct)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        _ = Task.Run(() =>
        {
            try { WaitHandle.WaitAny([ev, ct.WaitHandle]); if (!ct.IsCancellationRequested) { Log.Info("quit requested by another process"); quit(); } }
            finally { ev.Dispose(); }
        }, CancellationToken.None);
    }

    public static int Quit(int waitSeconds)
    {
        if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var ev)) { return 0; }       // no engine running
        using (ev) { ev.Set(); }
        var until = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (DateTime.UtcNow < until)
        {
            using var m = new Mutex(false, MutexName);
            bool free = false;
            try { free = m.WaitOne(0); if (free) { m.ReleaseMutex(); } } catch (AbandonedMutexException) { free = true; }
            if (free) { return 0; }
            Thread.Sleep(300);
        }
        return 1;
    }

    public static (long Uploads, long Ops) Pending(EnginePaths paths)
    {
        if (!File.Exists(paths.IndexPath)) { return (0, 0); }
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        using var index = new NoDrive.Core.Index.ItemIndex(paths.IndexPath);
        return (index.CountUploads(), index.CountOps());
    }

    public static int PendingExitCode(EnginePaths paths)
    {
        var (u, o) = Pending(paths);
        Console.WriteLine($"{u} upload(s) and {o} delete/rename operation(s) not yet synced");
        return u + o > 0 ? 3 : 0;
    }

    public static int Uninstall(EnginePaths paths, bool force, bool deleteData, bool deleteRoot, int waitSeconds)
    {
        // Look at the journal BEFORE stopping the engine: a refused uninstall must leave NoDrive running (a stopped provider makes every placeholder unreadable in Explorer).
        if (!force && PendingExitCode(paths) != 0) { Console.WriteLine("Unsynced changes would be lost. Wait for NoDrive to finish (the tray icon or the settings page shows what is pending), or use --force."); return 3; }
        if (Quit(waitSeconds) != 0) { Console.WriteLine("NoDrive is still running; close it first."); return 1; }
        if (!force && PendingExitCode(paths) != 0) { Console.WriteLine("Unsynced changes would be lost. Start NoDrive and wait for it to finish, or use --force."); return 3; }
        SyncRootRegistrar.Unregister();
        NavigationPane.RemoveLegacyEntry();
        UserSettings.SetAutostart(false);
        if (deleteRoot && Directory.Exists(paths.SyncRoot)) { Directory.Delete(paths.SyncRoot, recursive: true); }
        if (deleteData && Directory.Exists(paths.DataDir)) { try { Directory.Delete(paths.DataDir, recursive: true); } catch (IOException ex) { Console.WriteLine($"could not remove the data folder: {ex.Message}"); } }
        Console.WriteLine("NoDrive removed from this PC (downloaded files stay on disk unless --delete-root was used).");
        return 0;
    }

    /// <summary>Crash recovery: after an unhandled exception start a fresh engine, at most three times in ten minutes, so a crash loop cannot spin.</summary>
    public static void InstallCrashRestart(EnginePaths paths)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                Log.Error($"unhandled exception, restarting: {e.ExceptionObject}");
                try { File.WriteAllText(Path.Combine(paths.LogDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), $"{Diagnostics.Header()}{Environment.NewLine}{e.ExceptionObject}"); } catch (IOException) { }
                string file = Path.Combine(paths.DataDir, "restarts.txt");
                var recent = File.Exists(file) ? File.ReadAllLines(file).Select(l => DateTime.TryParse(l, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.MinValue).Where(d => DateTime.UtcNow - d < TimeSpan.FromMinutes(10)).ToList() : [];
                if (recent.Count < 3 && Environment.ProcessPath is { } exe)
                {
                    recent.Add(DateTime.UtcNow);
                    File.WriteAllLines(file, recent.Select(d => d.ToString("O")));
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
                }
            }
            catch (Exception) { /* nothing more we can do while dying */ }
        };
    }
}
