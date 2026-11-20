using System.Diagnostics;
using System.Runtime.InteropServices;
using NoDrive.Engine.CloudFiles;

namespace NoDrive.Engine.Infrastructure;

/// <summary>
/// Store build only. An MSIX has no uninstall hook, and Windows sends the same close messages (WM_ENDSESSION with ENDSESSION_CLOSEAPP) for an update and for a removal.
/// So the engine hands over to a short-lived helper that runs OUTSIDE the package (started through Task Scheduler, from a copy in %TEMP%, because Windows kills everything
/// the package started) and decides afterwards: package still installed means it was an update (nothing to do); package gone for good means a removal, and the helper
/// unregisters the sync root so Explorer is left clean. Downloaded files stay. Nothing happens when work is pending, on shutdown, restart or logoff.
/// </summary>
internal static partial class PackageRemoval
{
    private const uint EndSessionCloseApp = 0x1, EndSessionLogoff = 0x80000000;
    private const string TaskName = "NoDriveStoreCleanup";
    private static EnginePaths? s_paths;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetPackagesByPackageFamily(string familyName, ref uint count, nint* fullNames, ref uint bufferLength, char* buffer);
    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetCurrentPackageFamilyName(ref uint length, char* name);

    public static void Init(EnginePaths paths) => s_paths = paths;

    /// <summary>Called from the window procedure when Windows asks the app to close. Cheap checks first; at most one short process start. Never throws.</summary>
    public static void OnCloseRequested(nuint flags)
    {
        try
        {
            if (!PackageIdentity.IsPackaged || s_paths is null || (flags & EndSessionLogoff) != 0 || (flags & EndSessionCloseApp) == 0) { return; }   // plain shutdown/restart/logoff: never clean up
            var (uploads, ops) = Lifecycle.Pending(s_paths);
            if (uploads + ops > 0) { Log.Info("package closing with unsynced changes: leaving the sync root registered"); return; }
            string? family = FamilyName();
            if (family is null || Environment.ProcessPath is not { } exe) { return; }
            string copy = Path.Combine(Path.GetTempPath(), "NoDrive-cleanup.exe");
            File.Copy(exe, copy, overwrite: true);
            string command = $"\\\"{copy}\\\" --cleanup-after-removal {family}";
            Run("schtasks.exe", $"/Create /TN {TaskName} /TR \"{command}\" /SC ONCE /ST 00:00 /F");
            Run("schtasks.exe", $"/Run /TN {TaskName}");
            Log.Info("package closing: started the clean-up watcher (it removes the sync root only if the package is gone for good)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Warn($"could not start the clean-up watcher: {ex.Message}"); }
    }

    private static void Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        p?.WaitForExit(4000);
    }

    private static unsafe string? FamilyName()
    {
        uint len = 0;
        GetCurrentPackageFamilyName(ref len, null);
        if (len == 0) { return null; }
        char* buf = stackalloc char[(int)len];
        return GetCurrentPackageFamilyName(ref len, buf) == 0 ? new string(buf) : null;
    }

    private static unsafe bool Installed(string family)
    {
        uint count = 0, size = 0;
        int rc = GetPackagesByPackageFamily(family, ref count, null, ref size, null);
        return rc != 0 || count > 0;   // 0 with count 0 = none; ERROR_INSUFFICIENT_BUFFER (122) = at least one; any other error: assume installed (never clean up on doubt)
    }

    /// <summary>Entry point of the helper (--cleanup-after-removal FAMILY). Runs unpackaged. Returns 0 when it did nothing or finished.</summary>
    public static int Watch(string family)
    {
        string log = Path.Combine(Path.GetTempPath(), "NoDrive-cleanup.log");
        void Note(string s) { try { File.AppendAllText(log, $"{DateTime.Now:s} {s}{Environment.NewLine}"); } catch (IOException) { } }
        try
        {
            Thread.Sleep(10_000);                        // an update re-registers the package within seconds
            int absent = 0;
            for (int i = 0; i < 12; i++)                 // up to about a minute
            {
                if (Installed(family)) { Note("package present again: it was an update, nothing to do"); return 0; }
                absent++;
                Thread.Sleep(5_000);
            }
            if (absent < 12) { return 0; }
            SyncRootRegistrar.Unregister();
            if (SyncRootRegistrar.IsRegistered())
            {
                // The registration was made from inside the package, which the unpackaged API call may not be allowed to remove: try the Win32 call, then the registry key itself.
                var hr = Windows.Win32.PInvoke.CfUnregisterSyncRoot(SyncRootRegistrar.RegisteredPath() ?? "");
                Note($"CfUnregisterSyncRoot: 0x{hr.Value:X}");
                if (SyncRootRegistrar.IsRegistered()) { Note($"registry removal: {SyncRootRegistrar.TryDeleteKey()}"); }
            }
            NavigationPane.RemoveLegacyEntry();
            Note($"package removed: sync root unregistered (still registered afterwards: {SyncRootRegistrar.IsRegistered()})");
            return 0;
        }
        finally
        {
            try { Run("schtasks.exe", $"/Delete /TN {TaskName} /F"); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            // The helper cannot delete its own image; ask cmd to do it once this process has exited.
            try { Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & del /q \"{Environment.ProcessPath}\"") { UseShellExecute = false, CreateNoWindow = true }); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
    }
}
