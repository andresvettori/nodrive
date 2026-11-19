using Microsoft.Win32;

namespace NoDrive.Engine.Infrastructure;

/// <summary>Pause state (survives restarts via a flag file) and the per-user autostart entry.</summary>
internal static class UserSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "NoDrive";
    private static string s_pauseFile = "";
    private static volatile bool s_paused;

    /// <summary>True while syncing (ingest, upload, delete/rename replay) is paused. On-demand downloads keep working.</summary>
    public static bool Paused => s_paused;

    public static void Init(string dataDir)
    {
        s_pauseFile = Path.Combine(dataDir, "paused.flag");
        s_paused = File.Exists(s_pauseFile);
    }

    public static void SetPaused(bool paused)
    {
        s_paused = paused;
        try
        {
            if (paused) { File.WriteAllText(s_pauseFile, "paused"); }
            else if (File.Exists(s_pauseFile)) { File.Delete(s_pauseFile); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"could not persist the pause state: {ex.Message}"); }
    }

    public static bool AutostartEnabled
    {
        get
        {
            if (PackageIdentity.IsPackaged) { return StartupTaskState() == Windows.ApplicationModel.StartupTaskState.Enabled; }
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is string;
        }
    }

    /// <summary>The Store build's startup task (declared in the package manifest); the user can also switch it in Settings > Apps > Startup.</summary>
    private static Windows.ApplicationModel.StartupTaskState? StartupTaskState()
    {
        try { return Windows.ApplicationModel.StartupTask.GetAsync("NoDriveStartup").AsTask().GetAwaiter().GetResult().State; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { Log.Warn($"startup task state unavailable: {ex.Message}"); return null; }
    }

    public static void SetAutostart(bool enabled)
    {
        if (PackageIdentity.IsPackaged)
        {
            try
            {
                var task = Windows.ApplicationModel.StartupTask.GetAsync("NoDriveStartup").AsTask().GetAwaiter().GetResult();
                if (enabled) { _ = task.RequestEnableAsync().AsTask().GetAwaiter().GetResult(); } else { task.Disable(); }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { Log.Warn($"autostart change failed: {ex.Message}"); }
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe) { key.SetValue(RunName, $"\"{exe}\"", RegistryValueKind.String); }
            else { key.DeleteValue(RunName, throwOnMissingValue: false); }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { Log.Warn($"autostart change failed: {ex.Message}"); }
    }
}
