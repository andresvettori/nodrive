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
    public const string ProviderVersion = "0.1.0";
    public static readonly Guid ProviderId = new("3b0c8d5e-6a47-4f43-9c1e-5d2f7a9b1c42");

    public static string SyncRootId => $"NoDrive!{WindowsIdentity.GetCurrent().User!.Value}!Personal";
    private static string KeyPath => @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\" + SyncRootId;

    public static bool IsRegistered()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return key is not null;
    }

    public static int? ReadFlags()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return key?.GetValue("Flags") as int?;
    }

    /// <summary>Registers (only when not yet registered) and makes sure Flags are 1058. Returns a short human-readable outcome.</summary>
    public static async Task<(bool Fresh, string Outcome)> EnsureRegisteredAsync(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        string outcome;
        bool fresh = !IsRegistered();
        if (fresh)
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
                HydrationPolicy = StorageProviderHydrationPolicy.Progressive,
                HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
                PopulationPolicy = StorageProviderPopulationPolicy.Full,                // on demand; the engine prefetches heavy folders itself (E3c)
                InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime | StorageProviderInSyncPolicy.DirectoryLastWriteTime,
                HardlinkPolicy = StorageProviderHardlinkPolicy.None,
                ShowSiblingsAsGroup = false,
                AllowPinning = true,
                Context = CryptographicBuffer.ConvertStringToBinary(SyncRootId, BinaryStringEncoding.Utf8),
            };
            StorageProviderSyncRootManager.Register(info);
            outcome = "registered";
        }
        else { outcome = "already registered (not re-registered: that would reset the shell flags)"; }

        if (ReadFlags() != ShellIntegrationFlags)
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
