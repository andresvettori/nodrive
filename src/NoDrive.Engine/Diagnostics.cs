using System.Runtime.InteropServices;
using System.Text;
using NoDrive.Core.Index;
using NoDrive.Engine.CloudFiles;
using NoDrive.Engine.Infrastructure;
using NoDrive.Engine.Web;

namespace NoDrive.Engine;

/// <summary>
/// One place that knows how to describe this installation: version, session id, system, registration, index health, queues and recent errors. Used by `--doctor`, the support report and the page.
/// Contains no secrets (never reads the token file's content).
/// </summary>
internal static class Diagnostics
{
    /// <summary>Short id of this run, written in the log header and shown on the page so a report can be matched to its log.</summary>
    public static readonly string SessionId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
    public static string Version => SyncRootRegistrar.ProviderVersion;
    private static readonly DateTime Started = DateTime.UtcNow;

    public static string Header() =>
        $"NoDrive {Version} session {SessionId} | {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture} | language {WebUi.OsLanguage()}, region {WebUi.OsCulture()}, time zone {TimeZoneInfo.Local.Id}";

    /// <summary>Runs the checks and returns the report text; <paramref name="failed"/> is true when something needs attention.</summary>
    public static string Report(EnginePaths paths, EngineConfig config, ItemIndex? liveIndex, out bool failed)
    {
        var sb = new StringBuilder();
        bool bad = false;
        void Line(string state, string name, string detail) { sb.Append('[').Append(state).Append("] ").Append(name).Append(": ").AppendLine(detail); if (state == "FAIL") { bad = true; } }
        sb.AppendLine(Header());
        sb.AppendLine($"report time {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}; engine running {(DateTime.UtcNow - Started).TotalMinutes:F0} min; installed at {Environment.ProcessPath}");
        sb.AppendLine();

        bool reg = SyncRootRegistrar.IsRegistered();
        Line(reg ? "OK" : "FAIL", "sync root registered", reg ? $"yes, shell flags {SyncRootRegistrar.ReadFlags()} (expected {SyncRootRegistrar.ShellIntegrationFlags}), policy marker {config.HydrationPolicy}" : "no: NoDrive has not registered its folder with Windows");
        string? registeredPath = reg ? SyncRootRegistrar.RegisteredPath() : null;
        if (registeredPath is not null && !string.Equals(registeredPath.TrimEnd('\\'), paths.SyncRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { Line("WARN", "sync root path", $"registered {registeredPath}, configured {paths.SyncRoot}"); }
        bool exists = Directory.Exists(paths.SyncRoot);
        Line(exists ? "OK" : "FAIL", "sync folder", $"{paths.SyncRoot} {(exists ? "exists" : "is missing")}");
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(paths.SyncRoot)!);
            long freeMb = drive.AvailableFreeSpace / 1048576;
            Line(freeMb > 1024 ? "OK" : freeMb > 256 ? "WARN" : "FAIL", "free disk space", $"{freeMb:N0} MB free on {drive.Name} ({drive.DriveFormat})");
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) { Line("FAIL", "file system", "the Cloud Files API needs NTFS"); }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException) { Line("WARN", "free disk space", ex.Message); }
        Line(File.Exists(paths.TokenPath) ? "OK" : "WARN", "sign-in", File.Exists(paths.TokenPath) ? "a saved sign-in exists (content not read)" : "no saved sign-in: the account must sign in");

        ItemIndex? idx = liveIndex;
        bool opened = false;
        try
        {
            if (idx is null && File.Exists(paths.IndexPath))
            {
                SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
                idx = new ItemIndex(paths.IndexPath);
                opened = true;
            }
            if (idx is null) { Line("WARN", "index", "no index yet (first start)"); }
            else
            {
                string? problem = idx.QuickCheck();
                Line(problem is null ? "OK" : "FAIL", "index integrity", problem ?? $"healthy, {new FileInfo(paths.IndexPath).Length / 1048576:N0} MB");
                sb.AppendLine($"       items {idx.Count():N0}, on disk {idx.CountLocal():N0}, uploads waiting {idx.CountUploads():N0}, delete/rename waiting {idx.CountOps():N0}");
            }
        }
        catch (Exception ex) { Line("FAIL", "index", ex.Message); }
        finally { if (opened) { idx?.Dispose(); } }

        var co = Coexistence.Detect(paths.SyncRoot);
        Line(co.NoDriveInsideOneDrive ? "FAIL" : "OK", "OneDrive app", $"{(co.OneDriveRunning ? "running" : co.Folder is null ? "not installed" : "installed, not running")}{(co.Email is null ? "" : ", " + co.Email)}; folder backup: {(co.BackedUpFolders.Length == 0 ? "off" : string.Join(", ", co.BackedUpFolders))}{(co.NoDriveInsideOneDrive ? "; THE NODRIVE FOLDER IS INSIDE THE ONEDRIVE FOLDER" : "")}");
        Line(UserSettings.Paused ? "WARN" : "OK", "syncing", UserSettings.Paused ? "paused by the user" : "active");
        Line(UserSettings.AutostartEnabled ? "OK" : "WARN", "start with Windows", UserSettings.AutostartEnabled ? "on" : "off");
        sb.AppendLine($"       limits: download {(config.DownloadKiBps == 0 ? "none" : config.DownloadKiBps + " KiB/s")}, upload {(config.UploadKiBps == 0 ? "none" : config.UploadKiBps + " KiB/s")}; language setting {config.Language}; workers {config.HydrationWorkers}");

        try
        {
            long logBytes = Directory.Exists(paths.LogDir) ? new DirectoryInfo(paths.LogDir).EnumerateFiles().Sum(f => f.Length) : 0;
            sb.AppendLine($"       logs {logBytes / 1024:N0} KB in {paths.LogDir}");
            var latest = Directory.Exists(paths.LogDir) ? new DirectoryInfo(paths.LogDir).EnumerateFiles("engine-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault() : null;
            if (latest is not null)
            {
                var errors = ReadLines(latest.FullName).Where(l => l.Contains(" WARN ", StringComparison.Ordinal) || l.Contains(" ERROR ", StringComparison.Ordinal)).TakeLast(15).ToList();
                sb.AppendLine().AppendLine(errors.Count == 0 ? "no warnings or errors in the latest log" : "latest warnings and errors:");
                foreach (var e in errors) { sb.AppendLine("  " + e); }
            }
        }
        catch (IOException) { /* log in use: skip */ }
        failed = bad;
        return sb.ToString();
    }

    /// <summary>Reads a log file that the engine may be appending to.</summary>
    internal static IEnumerable<string> ReadLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = r.ReadLine()) is not null) { yield return line; }
    }
}
