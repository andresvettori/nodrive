using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using NoDrive.Core.Index;
using NoDrive.Engine.Infrastructure;

namespace NoDrive.Engine;

/// <summary>
/// The "report a problem" file: one zip on the Desktop with the logs, the recent activity, a health report and the Windows cloud-files event log, plus a README that says what it contains.
/// It never contains the sign-in token or any file content. File names appear in logs and activity; the user can leave them out (names are then masked on a best-effort basis).
/// </summary>
internal static partial class SupportBundle
{
    public static string Create(EnginePaths paths, EngineConfig config, ItemIndex? index, bool includeNames)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) { desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        string zip = Path.Combine(desktop, $"NoDrive-report-{DateTime.Now:yyyyMMdd-HHmmss}-{Diagnostics.SessionId}.zip");
        using var fs = new FileStream(zip, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);

        string report = Diagnostics.Report(paths, config, index, out _);
        Add(archive, "system-report.txt", Mask(report, includeNames, paths));
        Add(archive, "README.txt", $"NoDrive problem report\r\nVersion {Diagnostics.Version}, session {Diagnostics.SessionId}, created {DateTime.Now:yyyy-MM-dd HH:mm:ss}.\r\n\r\nWhat is inside: system-report.txt (health checks), logs\\ (the last 7 days of engine logs and crash notes), activity.jsonl (recent transfers and outcomes){(includeNames ? "" : ", omitted because you chose to leave file names out")}, config.json (your settings, no sign-in data), cloudfiles-events.txt (Windows' own cloud-files log, if available).\r\nNot inside: your sign-in token, your passwords, or the content of any file.\r\n{(includeNames ? "File names appear in the logs and the activity list." : "File names were masked on a best-effort basis; if something sensitive remains, delete it before sending.")}\r\n\r\nSend this zip to the person who gave you NoDrive and tell them what you were doing when the problem happened.\r\n");
        if (Directory.Exists(paths.LogDir))
        {
            foreach (var f in new DirectoryInfo(paths.LogDir).EnumerateFiles().Where(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-7)).OrderBy(f => f.Name))
            {
                try { Add(archive, "logs/" + f.Name, Mask(string.Join("\r\n", Diagnostics.ReadLines(f.FullName)), includeNames, paths)); } catch (IOException) { /* skip a file that is locked right now */ }
            }
        }
        string activity = Path.Combine(paths.DataDir, "activity.jsonl");
        if (includeNames && File.Exists(activity)) { try { Add(archive, "activity.jsonl", string.Join("\r\n", Diagnostics.ReadLines(activity))); } catch (IOException) { } }
        if (File.Exists(paths.ConfigPath)) { try { Add(archive, "config.json", string.Join("\r\n", Diagnostics.ReadLines(paths.ConfigPath))); } catch (IOException) { } }
        Add(archive, "cloudfiles-events.txt", Mask(WindowsEvents(), includeNames, paths));
        Log.Info($"support report created: {zip} (names {(includeNames ? "included" : "masked")})");
        return zip;
    }

    private static void Add(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    /// <summary>The Windows Cloud Files operational log (best effort: needs the channel to exist and finishes within 10 s).</summary>
    private static string WindowsEvents()
    {
        try
        {
            var psi = new ProcessStartInfo("wevtutil.exe", "qe Microsoft-Windows-CloudFiles/Operational /c:150 /rd:true /f:text") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) { return "not available"; }
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch (InvalidOperationException) { } return "timed out"; }
            return output.Result.Length == 0 ? "no events" : output.Result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return "not available: " + ex.Message; }
    }

    /// <summary>Best-effort masking of names and paths when the user prefers to leave them out (see <see cref="NoDrive.Core.Support.LogMasker"/>).</summary>
    internal static string Mask(string text, bool includeNames, EnginePaths paths) => includeNames ? text : NoDrive.Core.Support.LogMasker.Mask(text, paths.SyncRoot);
}
