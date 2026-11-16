using System.Diagnostics;
using Microsoft.Win32;

namespace NoDrive.Engine.Infrastructure;

/// <summary>What the official OneDrive client is doing on this PC (read-only detection; NoDrive never touches OneDrive's settings or files).</summary>
internal sealed record CoexistenceInfo(bool OneDriveRunning, string? Email, string? Folder, string[] BackedUpFolders, bool NoDriveInsideOneDrive);

/// <summary>
/// Detects OneDrive.exe, its personal-account folder and Known Folder Backup (Desktop, Documents, Pictures redirected into the OneDrive folder).
/// Cached for 30 s: it lists processes and reads a few registry values. Coexistence is allowed (like a second computer on the same account);
/// the page explains the rules instead of blocking.
/// </summary>
internal static class Coexistence
{
    private static CoexistenceInfo? s_cached;
    private static DateTime s_at;

    public static CoexistenceInfo Detect(string syncRoot)
    {
        if (s_cached is not null && DateTime.UtcNow - s_at < TimeSpan.FromSeconds(30)) { return s_cached; }
        string? email = null, folder = null;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts\Personal");
            email = k?.GetValue("UserEmail") as string;
            folder = k?.GetValue("UserFolder") as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { /* not readable: treat as not installed */ }

        bool running = false;
        try { var p = Process.GetProcessesByName("OneDrive"); running = p.Length > 0; foreach (var x in p) { x.Dispose(); } }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* cannot list processes */ }

        var backed = new List<string>();
        if (!string.IsNullOrEmpty(folder))
        {
            foreach (var (name, special) in new[] { ("Desktop", Environment.SpecialFolder.DesktopDirectory), ("Documents", Environment.SpecialFolder.MyDocuments), ("Pictures", Environment.SpecialFolder.MyPictures) })
            {
                string path = Environment.GetFolderPath(special);
                if (path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) { backed.Add(name); }
            }
        }
        bool inside = !string.IsNullOrEmpty(folder) && syncRoot.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        s_at = DateTime.UtcNow;
        return s_cached = new CoexistenceInfo(running, email, folder, [.. backed], inside);
    }
}
