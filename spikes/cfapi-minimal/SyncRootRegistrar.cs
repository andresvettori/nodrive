using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace NoDrive.Spikes.CfApiMinimal;

/// <summary>
/// Registers / unregisters the spike sync root. Preferred path: WinRT StorageProviderSyncRootManager
/// (gives the navigation-pane node and Explorer branding). Fallback: Win32 CfRegisterSyncRoot (filter
/// registration only). Which one worked is one of the facts P0 step 3 must record.
/// </summary>
internal static class SyncRootRegistrar
{
    public const string ProviderName = "NoDrive Spike";
    public const string ProviderVersion = "0.0.1";
    public static readonly Guid ProviderId = new("7f0d1a6e-2b3c-4d5e-9f10-a1b2c3d4e5f6");

    public static string SyncRootId => $"NoDriveSpike!{WindowsIdentity.GetCurrent().User!.Value}!Personal";

    /// <param name="partialPopulation">true = populate on demand (WinRT StorageProviderPopulationPolicy.Full: FETCH_PLACEHOLDERS answers a whole folder when it is first opened; the WinRT enum has no 'Partial').</param>
    public static async Task<string> RegisterAsync(string rootPath, bool forceWin32, bool partialPopulation = false)
    {
        Directory.CreateDirectory(rootPath);
        if (!forceWin32)
        {
            try
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
                    PopulationPolicy = partialPopulation ? StorageProviderPopulationPolicy.Full : StorageProviderPopulationPolicy.AlwaysFull,
                    InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime | StorageProviderInSyncPolicy.DirectoryLastWriteTime,
                    HardlinkPolicy = StorageProviderHardlinkPolicy.None,
                    ShowSiblingsAsGroup = false,
                    // Without this the shell hides "Always keep on this device" / "Free up space" (OneDrive's registry Flags = ours + 0x400).
                    AllowPinning = true,
                    Context = CryptographicBuffer.ConvertStringToBinary(SyncRootId, BinaryStringEncoding.Utf8),
                };
                StorageProviderSyncRootManager.Register(info);
                return "winrt:StorageProviderSyncRootManager.Register";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WinRT registration failed ({ex.GetType().Name}: {ex.Message}); falling back to CfRegisterSyncRoot");
            }
        }

        RegisterWin32(rootPath);
        return "win32:CfRegisterSyncRoot";
    }

    private static unsafe void RegisterWin32(string rootPath)
    {
        byte[] identity = Encoding.UTF8.GetBytes(SyncRootId);
        fixed (char* pPath = rootPath)
        fixed (char* pName = ProviderName)
        fixed (char* pVersion = ProviderVersion)
        fixed (byte* pIdentity = identity)
        {
            var reg = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)sizeof(CF_SYNC_REGISTRATION),
                ProviderName = new PCWSTR(pName),
                ProviderVersion = new PCWSTR(pVersion),
                SyncRootIdentity = pIdentity,
                SyncRootIdentityLength = (uint)identity.Length,
                ProviderId = ProviderId,
            };
            var policies = new CF_SYNC_POLICIES
            {
                StructSize = (uint)sizeof(CF_SYNC_POLICIES),
                Hydration = new CF_HYDRATION_POLICY
                {
                    Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_PROGRESSIVE,
                    Modifier = CF_HYDRATION_POLICY_MODIFIER.CF_HYDRATION_POLICY_MODIFIER_AUTO_DEHYDRATION_ALLOWED,
                },
                Population = new CF_POPULATION_POLICY
                {
                    Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_ALWAYS_FULL,
                    Modifier = CF_POPULATION_POLICY_MODIFIER.CF_POPULATION_POLICY_MODIFIER_NONE,
                },
                InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_TRACK_FILE_LAST_WRITE_TIME | CF_INSYNC_POLICY.CF_INSYNC_POLICY_TRACK_DIRECTORY_LAST_WRITE_TIME,
                HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_NONE,
                PlaceholderManagement = CF_PLACEHOLDER_MANAGEMENT_POLICY.CF_PLACEHOLDER_MANAGEMENT_POLICY_DEFAULT,
            };
            HRESULT hr = PInvoke.CfRegisterSyncRoot(new PCWSTR(pPath), &reg, &policies, CF_REGISTER_FLAGS.CF_REGISTER_FLAG_NONE);
            Marshal.ThrowExceptionForHR(hr.Value);
        }
    }

    /// <summary>
    /// Diagnostic: registers throwaway sync roots with different StorageProviderSyncRootInfo property combinations and prints the
    /// SyncRootManager registry values Windows wrote (notably Flags), to learn which API property yields which shell integration bits.
    /// </summary>
    public static async Task ProbeFlagsAsync(string baseDir)
    {
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        var variants = new (string Name, Action<StorageProviderSyncRootInfo> Tweak)[]
        {
            ("baseline (AllowPinning)", i => { }),
            ("ProtectionMode=Personal", i => i.ProtectionMode = StorageProviderProtectionMode.Personal),
            ("ProtectionMode=Unknown", i => i.ProtectionMode = StorageProviderProtectionMode.Unknown),
            ("ShowSiblingsAsGroup=true", i => i.ShowSiblingsAsGroup = true),
            ("RecycleBinUri", i => i.RecycleBinUri = new Uri("https://onedrive.live.com/?v=RecycleBin")),
            ("ItemPropertyDefinition", i => i.StorageProviderItemPropertyDefinitions.Add(new StorageProviderItemPropertyDefinition { DisplayNameResource = "NoDrive state", Id = 1 })),
            ("Personal+RecycleBin+Siblings+Property", i =>
            {
                i.ProtectionMode = StorageProviderProtectionMode.Personal;
                i.RecycleBinUri = new Uri("https://onedrive.live.com/?v=RecycleBin");
                i.ShowSiblingsAsGroup = true;
                i.StorageProviderItemPropertyDefinitions.Add(new StorageProviderItemPropertyDefinition { DisplayNameResource = "NoDrive state", Id = 1 });
            }),
        };
        int n = 0;
        foreach (var (name, tweak) in variants)
        {
            n++;
            string dir = Path.Combine(baseDir, $"v{n}");
            Directory.CreateDirectory(dir);
            string id = $"NoDriveProbe!{sid}!v{n}";
            try
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(dir);
                var info = new StorageProviderSyncRootInfo
                {
                    Id = id,
                    Path = folder,
                    DisplayNameResource = $"NoDrive probe {n}",
                    IconResource = $"{Environment.ProcessPath},0",
                    Version = ProviderVersion,
                    ProviderId = ProviderId,
                    HydrationPolicy = StorageProviderHydrationPolicy.Progressive,
                    HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
                    PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
                    InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime | StorageProviderInSyncPolicy.DirectoryLastWriteTime,
                    HardlinkPolicy = StorageProviderHardlinkPolicy.None,
                    AllowPinning = true,
                    Context = CryptographicBuffer.ConvertStringToBinary(id, BinaryStringEncoding.Utf8),
                };
                tweak(info);
                StorageProviderSyncRootManager.Register(info);
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\" + id);
                var values = key is null ? "(key not found)" : string.Join("; ", key.GetValueNames().Where(v => v is not ("DisplayNameResource" or "IconResource")).Select(v => $"{v}={key.GetValue(v)}"));
                Console.WriteLine($"probe {n,-2} {name,-40} -> {values}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"probe {n,-2} {name,-40} -> FAILED {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { StorageProviderSyncRootManager.Unregister(id); } catch { }
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        try { Directory.Delete(baseDir, true); } catch { }
    }

    public static unsafe void Unregister(string rootPath)
    {
        try
        {
            StorageProviderSyncRootManager.Unregister(SyncRootId);
            Console.WriteLine("unregistered via StorageProviderSyncRootManager.Unregister");
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WinRT Unregister failed ({ex.Message}); trying CfUnregisterSyncRoot");
        }
        fixed (char* pPath = rootPath)
        {
            HRESULT hr = PInvoke.CfUnregisterSyncRoot(new PCWSTR(pPath));
            Console.WriteLine($"CfUnregisterSyncRoot -> 0x{hr.Value:X8}");
        }
    }

    /// <summary>Prints the policies the platform actually recorded for the sync root (to compare with what we asked for).</summary>
    public static unsafe void PrintEffectivePolicies(string rootPath)
    {
        const int size = 4096;
        byte* buf = stackalloc byte[size];
        uint returned;
        fixed (char* p = rootPath)
        {
            HRESULT hr = PInvoke.CfGetSyncRootInfoByPath(new PCWSTR(p), CF_SYNC_ROOT_INFO_CLASS.CF_SYNC_ROOT_INFO_STANDARD, buf, size, &returned);
            if (hr.Failed) { Console.WriteLine($"CfGetSyncRootInfoByPath(STANDARD) -> 0x{hr.Value:X8}"); return; }
        }
        var s = (CF_SYNC_ROOT_STANDARD_INFO*)buf;
        Console.WriteLine($"effective policies: hydration={s->HydrationPolicy.Primary}/{s->HydrationPolicy.Modifier} population={s->PopulationPolicy.Primary} insync={s->InSyncPolicy} hardlink={s->HardLinkPolicy} provider='{s->ProviderName}'");
    }

    /// <summary>The SyncRootManager Flags value that gives the native OneDrive-like Explorer integration (ADR-0009).</summary>
    public const int ShellIntegrationFlags = 1058;

    /// <summary>Reads the Flags value of our SyncRootManager key (readable without elevation); null when the key is missing.</summary>
    public static int? ReadShellFlags()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\" + SyncRootId);
        return key?.GetValue("Flags") as int?;
    }

    /// <summary>Writes Flags=1058 on our key. Needs an elevated token (the setup step); returns false with a reason otherwise.</summary>
    public static bool TryApplyShellFlags(out string reason)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\" + SyncRootId, writable: true);
            if (key is null) { reason = "key not found"; return false; }
            key.SetValue("Flags", ShellIntegrationFlags, Microsoft.Win32.RegistryValueKind.DWord);
            reason = "ok";
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            reason = "not elevated: " + ex.Message;
            return false;
        }
    }

    /// <summary>True when our own registration (from a previous run) exists in the shell's SyncRootManager key.</summary>
    public static bool IsRegisteredByUs()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\" + SyncRootId);
        return key is not null;
    }

    /// <summary>True when <paramref name="path"/> is already inside some provider's sync root (ours or a foreign one).</summary>
    public static unsafe bool IsUnderSyncRoot(string path, out string providerName)
    {
        providerName = "";
        const int size = 4096;
        byte* buf = stackalloc byte[size];
        uint returned;
        fixed (char* p = path)
        {
            HRESULT hr = PInvoke.CfGetSyncRootInfoByPath(new PCWSTR(p), CF_SYNC_ROOT_INFO_CLASS.CF_SYNC_ROOT_INFO_PROVIDER, buf, size, &returned);
            if (hr.Failed) { return false; }
        }
        var info = (CF_SYNC_ROOT_PROVIDER_INFO*)buf;
        providerName = info->ProviderName.ToString();
        return true;
    }
}
