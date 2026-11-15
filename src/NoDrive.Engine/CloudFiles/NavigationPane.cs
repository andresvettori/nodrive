using Microsoft.Win32;
using NoDrive.Engine.Infrastructure;

namespace NoDrive.Engine.CloudFiles;

/// <summary>
/// Pins the NoDrive folder in Explorer's navigation pane (next to Home, Gallery and OneDrive) with the NoDrive icon.
/// Per-user registry only (HKCU, no elevation); Explorer shows it after its next restart or sign-in. Idempotent.
/// </summary>
internal static class NavigationPane
{
    private const string Clsid = "{7d1f3a52-4c0e-4b8a-9a63-2e5f1c9d8b71}";
    private const string Classes = @"Software\Classes\CLSID\" + Clsid;
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    public static void Ensure(string rootPath, string? exePath)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Classes))
            {
                k.SetValue("", ProviderDisplayName);
                k.SetValue("System.IsPinnedToNameSpaceTree", 1, RegistryValueKind.DWord);
                k.SetValue("SortOrderIndex", 0x42, RegistryValueKind.DWord);
            }
            if (exePath is not null) { Set(Classes + @"\DefaultIcon", "", $"{exePath},0"); }
            Set(Classes + @"\InProcServer32", "", @"%SystemRoot%\system32\shell32.dll", "ThreadingModel", "Both");
            using (var k = Registry.CurrentUser.CreateSubKey(Classes + @"\ShellFolder"))
            {
                k.SetValue("FolderValueFlags", 0x28, RegistryValueKind.DWord);
                k.SetValue("Attributes", unchecked((int)0xF080004D), RegistryValueKind.DWord);
            }
            Set(Classes + @"\Instance", "CLSID", "{0E5AAE11-A475-4c5b-AB00-C66DE400274E}");
            using (var k = Registry.CurrentUser.CreateSubKey(Classes + @"\Instance\InitPropertyBag"))
            {
                k.SetValue("Attributes", 0x11, RegistryValueKind.DWord);
                k.SetValue("TargetFolderPath", rootPath);
            }
            Set($@"{Explorer}\Desktop\NameSpace\{Clsid}", "", ProviderDisplayName);
            using (var k = Registry.CurrentUser.CreateSubKey($@"{Explorer}\HideDesktopIcons\NewStartPanel")) { k.SetValue(Clsid, 1, RegistryValueKind.DWord); }   // not a desktop icon
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { Log.Warn($"navigation pane entry not created: {ex.Message}"); }
    }

    public static void Remove()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(Classes, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"{Explorer}\Desktop\NameSpace\{Clsid}", throwOnMissingSubKey: false);
            using var k = Registry.CurrentUser.OpenSubKey($@"{Explorer}\HideDesktopIcons\NewStartPanel", writable: true);
            k?.DeleteValue(Clsid, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { Log.Warn($"navigation pane cleanup: {ex.Message}"); }
    }

    private const string ProviderDisplayName = "NoDrive";

    private static void Set(string path, string name, string value, string? name2 = null, string? value2 = null)
    {
        using var k = Registry.CurrentUser.CreateSubKey(path);
        k.SetValue(name, value);
        if (name2 is not null) { k.SetValue(name2, value2!); }
    }
}
