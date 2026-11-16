using Microsoft.Win32;
using NoDrive.Engine.Infrastructure;

namespace NoDrive.Engine.CloudFiles;

/// <summary>
/// Windows already adds the sync root to Explorer's navigation pane (NamespaceCLSID, icon from the exe), so NoDrive creates no entry of its own.
/// The top group (Home, Gallery, OneDrive) is reserved for the OneDrive known folder (TargetKnownFolder); a third-party provider cannot join it
/// without taking over that Windows folder. This class only removes the duplicate entry an earlier build created (2026-11-15).
/// </summary>
internal static class NavigationPane
{
    private const string Clsid = "{7d1f3a52-4c0e-4b8a-9a63-2e5f1c9d8b71}";
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    public static void RemoveLegacyEntry()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\" + Clsid, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"{Explorer}\Desktop\NameSpace\{Clsid}", throwOnMissingSubKey: false);
            using var k = Registry.CurrentUser.OpenSubKey($@"{Explorer}\HideDesktopIcons\NewStartPanel", writable: true);
            k?.DeleteValue(Clsid, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { Log.Warn($"navigation pane cleanup: {ex.Message}"); }
    }
}
