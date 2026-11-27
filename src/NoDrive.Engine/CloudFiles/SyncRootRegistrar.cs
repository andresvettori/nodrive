using System.Security.Principal;
using Microsoft.Win32;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using NoDrive.Engine.Infrastructure;

namespace NoDrive.Engine.CloudFiles;

/// <summary>
/// Registers NoDrive's sync root and applies the Explorer-integration Flags value (ADR-0009).
/// Rules measured in P0: Register() writes Flags=34 (blank Status column, brown icons, no verbs); Flags=1058 gives the native OneDrive-like
/// experience; Users may write that key without elevation; and Register() on an EXISTING root resets Flags to 34, so a registered root
/// is never re-registered: it is only connected, and Flags are re-applied if they drifted.
/// </summary>
internal static class SyncRootRegistrar
{
    public const int ShellIntegrationFlags = 1058;
    public const string ProviderName = "NoDrive";
    public static string ProviderVersion => BuildInfo.Version;
    public static readonly Guid ProviderId = new("3b0c8d5e-6a47-4f43-9c1e-5d2f7a9b1c42");

    public static string SyncRootId => $"NoDrive!{WindowsIdentity.GetCurrent().User!.Value}!Personal";
    private static string KeyPath => @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\" + SyncRootId;

    public static bool IsRegistered()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return key is not null;
    }

    private static string? ReadMarker()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return key?.GetValue("NoDriveHydration") as string ?? "progressive";   // registrations before ADR-0010 have no marker
    }

    private static void WriteMarker(string value)
    {
        try { using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true); key?.SetValue("NoDriveHydration", value, RegistryValueKind.String); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { Log.Warn($"could not record the hydration policy: {ex.Message}"); }
    }

    /// <summary>The folder the current registration points at, or null when unavailable.</summary>
    public static string? RegisteredPath()
    {
        try { return StorageProviderSyncRootManager.GetSyncRootInformationForId(SyncRootId).Path.Path; }
        catch (Exception) { return null; }
    }

    public static int? ReadFlags()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return key?.GetValue("Flags") as int?;
    }

    /// <summary>Registers (only when not yet registered) and makes sure Flags are 1058. Returns a short human-readable outcome.</summary>
    public static async Task<(bool Fresh, string Outcome)> EnsureRegisteredAsync(string rootPath, string configuredPolicy = "full")
    {
        Directory.CreateDirectory(rootPath);
        string outcome;
        if (IsRegistered() && RegisteredPath() is { } oldPath && !string.Equals(oldPath.TrimEnd('\\'), rootPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"sync root moves from {oldPath} to {rootPath}: replacing the registration");
            Unregister();                                                // placeholders never opened are removed by Windows; downloaded files stay
        }
        bool fresh = !IsRegistered();
        // Full (default, ADR-0010): the whole file is downloaded on first access, like OneDrive; Photos and other packaged apps cancel slow partial reads and call the file corrupted.
        // NODRIVE_HYDRATION=progressive switches back (re-registers once).
        string wanted = string.Equals(Environment.GetEnvironmentVariable("NODRIVE_HYDRATION") ?? configuredPolicy, "progressive", StringComparison.OrdinalIgnoreCase) ? "progressive" : "full";
        bool policyChanged = !fresh && !string.Equals(ReadMarker(), wanted, StringComparison.Ordinal);
        if (fresh || policyChanged)
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(rootPath);
            var info = new StorageProviderSyncRootInfo
            {
                Id = SyncRootId,
                Path = folder,
                DisplayNameResource = ProviderName,
                IconResource = $"{Environment.ProcessPath},0",
                Version = ProviderVersion,
                ProviderId = ProviderId,
                HydrationPolicy = wanted == "full" ? StorageProviderHydrationPolicy.Full : StorageProviderHydrationPolicy.Progressive,
                HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
                PopulationPolicy = StorageProviderPopulationPolicy.Full,                // on demand; the engine prefetches heavy folders itself (E3c)
                InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime | StorageProviderInSyncPolicy.DirectoryLastWriteTime,
                HardlinkPolicy = StorageProviderHardlinkPolicy.None,
                ShowSiblingsAsGroup = false,
                AllowPinning = true,
                Context = CryptographicBuffer.ConvertStringToBinary(SyncRootId, BinaryStringEncoding.Utf8),
            };
            StorageProviderSyncRootManager.Register(info);
            outcome = fresh ? $"registered ({wanted})" : $"re-registered with the {wanted} hydration policy";
            WriteMarker(wanted);
        }
        else { outcome = "already registered (not re-registered: that would reset the shell flags)"; }

        if (PackageIdentity.IsPackaged)
        {
            // Store build: HKLM writes from inside the package go to a private copy that Explorer never reads, and reading it back looks fine. Write the real key from outside the package, every start (idempotent).
            string cmd = $"reg.exe add \"HKLM\\{KeyPath}\" /v Flags /t REG_DWORD /d {ShellIntegrationFlags} /f";
            try { await Task.Run(() => PackageRemoval.RunOutsidePackage("NoDriveShellFlags", cmd)); outcome += $"; shell flags {ShellIntegrationFlags} written outside the package"; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { outcome += $"; shell flags NOT applied ({ex.Message})"; }
        }
        else if (ReadFlags() != ShellIntegrationFlags)
        {
            outcome += TryApplyFlags(out string why) ? $"; shell flags set to {ShellIntegrationFlags}" : $"; shell flags NOT applied ({why}): Explorer verbs and Status column will be limited";
        }
        return (fresh, outcome);
    }

    public static bool TryApplyFlags(out string reason)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            if (key is null) { reason = "sync root key not found"; return false; }
            key.SetValue("Flags", ShellIntegrationFlags, RegistryValueKind.DWord);
            reason = "ok";
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>Removes the registration. WARNING (platform behaviour): unhydrated placeholders under the root are deleted from disk.</summary>
    public static void Unregister()
    {
        try { StorageProviderSyncRootManager.Unregister(SyncRootId); }
        catch (Exception ex) { Log.Warn($"Unregister: {ex.Message}"); }
    }

    /// <summary>Last resort for a registration the API will not remove: deletes the sync root key. Returns "ok" or the reason.</summary>
    public static string TryDeleteKey()
    {
        try { Registry.LocalMachine.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false); return IsRegistered() ? "still there" : "ok"; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { return ex.Message; }
    }

    /// <summary>True when <paramref name="path"/> already lies inside some other provider's sync root (for example OneDrive's folder).</summary>
    public static unsafe bool IsInsideForeignSyncRoot(string path, out string providerName)
    {
        providerName = "";
        if (!Directory.Exists(path)) { return false; }
        const int size = 4096;
        byte* buf = stackalloc byte[size];
        uint returned;
        fixed (char* p = path)
        {
            if (PInvoke.CfGetSyncRootInfoByPath(new PCWSTR(p), CF_SYNC_ROOT_INFO_CLASS.CF_SYNC_ROOT_INFO_PROVIDER, buf, size, &returned).Failed) { return false; }
        }
        providerName = ((CF_SYNC_ROOT_PROVIDER_INFO*)buf)->ProviderName.ToString();
        return !providerName.Equals(ProviderName, StringComparison.OrdinalIgnoreCase);
    }
}
