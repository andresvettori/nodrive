using System.Diagnostics;
using NoDrive.Engine.CloudFiles;
using NoDrive.Engine.Infrastructure;
using NoDrive.Engine.Shell;

namespace NoDrive.Engine;

/// <summary>
/// NoDrive engine entry point.
///   NoDrive.Engine.exe                      run (sign in on first start, register, sync, tray icon)
///   NoDrive.Engine.exe --status             print registration, shell flags and index state, then exit
///   NoDrive.Engine.exe --cleanup [--delete-root]   unregister the sync root (unhydrated placeholders are deleted by Windows)
///   options: --sync-root &lt;path&gt;  --data-dir &lt;path&gt;
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? rootArg = ArgValue(args, "--sync-root"), dataArg = ArgValue(args, "--data-dir");
        var bootPaths = EnginePaths.Create(rootArg, dataArg);
        Log.Init(bootPaths.LogDir);
        var config = EngineConfig.Load(bootPaths.ConfigPath);
        var paths = EnginePaths.Create(rootArg ?? config.SyncRoot, dataArg);

        if (args.Contains("--status")) { return Status(paths); }
        if (args.Contains("--cleanup")) { return Cleanup(paths, args.Contains("--delete-root")); }
        return Run(config, paths);
    }

    private static int Run(EngineConfig config, EnginePaths paths)
    {
        using var mutex = new Mutex(true, @"Local\NoDrive.Engine", out bool first);
        if (!first) { Log.Warn("NoDrive is already running."); return 1; }
        if (!File.Exists(paths.ConfigPath)) { config.Save(paths.ConfigPath); }

        using var cts = new CancellationTokenSource();
        using var service = new SyncService(config, paths);
        var serviceTask = Task.Run(async () =>
        {
            try { await service.RunAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error($"engine stopped: {ex}");
                TrayWindow.RequestQuit();
            }
        });

        TrayWindow.Create("NoDrive", onQuit: () => cts.Cancel(), onOpenFolder: () => OpenFolder(paths.SyncRoot), statusText: () => service.Status);
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                TrayWindow.SetTooltip("NoDrive: " + service.Status);
                try { await Task.Delay(TimeSpan.FromSeconds(5), cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        });
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; TrayWindow.RequestQuit(); };

        Log.Info($"NoDrive engine started (sync root {paths.SyncRoot}, data {paths.DataDir})");
        TrayWindow.RunMessageLoop();                       // sleeps in GetMessage: this is the idle cost of the whole engine
        cts.Cancel();
        SyncService.Shutdown();
        serviceTask.Wait(TimeSpan.FromSeconds(10));
        Log.Info("NoDrive engine stopped. " + service.Report());
        return 0;
    }

    private static int Status(EnginePaths paths)
    {
        Console.WriteLine($"sync root      : {paths.SyncRoot} (exists: {Directory.Exists(paths.SyncRoot)})");
        Console.WriteLine($"registered     : {SyncRootRegistrar.IsRegistered()}   shell flags: {SyncRootRegistrar.ReadFlags()} (want {SyncRootRegistrar.ShellIntegrationFlags})");
        Console.WriteLine($"token stored   : {File.Exists(paths.TokenPath)}");
        Console.WriteLine($"index          : {paths.IndexPath} ({(File.Exists(paths.IndexPath) ? new FileInfo(paths.IndexPath).Length / 1048576 + " MB" : "missing")})");
        if (File.Exists(paths.IndexPath))
        {
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
            using var index = new NoDrive.Core.Index.ItemIndex(paths.IndexPath);
            Console.WriteLine($"items          : {index.Count():N0}; delta link: {(index.GetMeta("delta.link") is not null)}; resuming: {(index.GetMeta("delta.next") is not null)}; populated folders: {index.PopulatedFolderIds().Count:N0}");
        }
        return 0;
    }

    private static int Cleanup(EnginePaths paths, bool deleteRoot)
    {
        SyncRootRegistrar.Unregister();
        Console.WriteLine("sync root unregistered (placeholders that were never opened are deleted by Windows; opened files stay as ordinary files).");
        if (deleteRoot && Directory.Exists(paths.SyncRoot))
        {
            Directory.Delete(paths.SyncRoot, recursive: true);
            Console.WriteLine($"deleted {paths.SyncRoot}");
        }
        return 0;
    }

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"could not open {path}: {ex.Message}"); }
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
