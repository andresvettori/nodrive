using System.Diagnostics;

namespace NoDrive.Engine.Infrastructure;

/// <summary>Opens a folder in File Explorer through the shell, so the window belongs to the user's running Explorer (a child explorer.exe started by the engine lacks its context and failed to browse cloud folders).</summary>
internal static class ShellOpen
{
    public static void Folder(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" }); }
        catch (Exception ex) { Log.Warn($"could not open {path}: {ex.Message}"); }
    }
}
