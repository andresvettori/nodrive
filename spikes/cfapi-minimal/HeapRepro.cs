using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;

namespace NoDrive.Spikes.CfApiMinimal;

/// <summary>
/// Stress test for the reported CfUpdatePlaceholder heap corruption on Windows 11 24H2/25H2
/// (github.com/iterate-ch/CloudFiles-HeapCorrupt: register, create one placeholder, then loop
/// CfOpenFileWithOplock -> CfReferenceProtectedHandle -> CfUpdatePlaceholder(no changes) -> release).
/// Variants: 0 = the exact repro, 1 = update with real metadata (what a sync engine does), 2 = several threads on different files.
/// A heap corruption would terminate this process with 0xC0000374 (visible as the exit code / Application Error event).
/// </summary>
internal static unsafe class HeapRepro
{
    public static int Run(string root, int variant, int iterations, int delayMs, int threads)
    {
        Directory.CreateDirectory(root);
        Register(root);
        var connection = Connect(root);
        Console.WriteLine($"heap-repro: variant={variant} iterations={iterations} delay={delayMs} ms threads={threads} build={Environment.OSVersion.Version} pid={Environment.ProcessId}");
        int files = variant == 2 ? threads : 1;
        for (int f = 0; f < files; f++) { CreatePlaceholder(root, $"__TEST{f}"); }

        var sw = Stopwatch.StartNew();
        long ok = 0, failed = 0;
        var workers = new List<Thread>();
        for (int f = 0; f < files; f++)
        {
            int idx = f;
            var t = new Thread(() =>
            {
                string path = Path.Combine(root, $"__TEST{idx}");
                for (int i = 0; i < iterations; i++)
                {
                    HRESULT hr = UpdateOnce(path, variant);
                    if (hr.Succeeded) { Interlocked.Increment(ref ok); } else { Interlocked.Increment(ref failed); if (failed < 5) { Console.WriteLine($"  update failed 0x{hr.Value:X8} at {i}"); } }
                    if (delayMs > 0) { Thread.Sleep(delayMs); }
                    if ((i + 1) % 1000 == 0 && idx == 0) { Console.WriteLine($"  {i + 1} iterations, {sw.Elapsed.TotalSeconds:F0} s"); }
                }
            });
            workers.Add(t);
            t.Start();
        }
        foreach (var t in workers) { t.Join(); }
        // Force the process heap to be walked so latent corruption is detected here rather than later.
        bool heapOk = HeapValidateProcess();
        PInvoke.CfDisconnectSyncRoot(connection);
        Console.WriteLine($"heap-repro finished: ok={ok} failed={failed} in {sw.Elapsed.TotalSeconds:F1} s; process heap validate = {(heapOk ? "OK" : "CORRUPT")}");
        return heapOk && failed == 0 ? 0 : 3;
    }

    /// <summary>A provider only gets to manage placeholders once connected (non-providers get 0x8007018B access denied); connect with no callbacks.</summary>
    private static CF_CONNECTION_KEY Connect(string root)
    {
        var table = stackalloc CF_CALLBACK_REGISTRATION[1];
        table[0] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE, Callback = null };
        CF_CONNECTION_KEY key;
        fixed (char* p = root)
        {
            HRESULT hr = PInvoke.CfConnectSyncRoot(new PCWSTR(p), table, null, CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, &key);
            Marshal.ThrowExceptionForHR(hr.Value);
        }
        return key;
    }

    private static bool HeapValidateProcess()
    {
        HANDLE heap = PInvoke.GetProcessHeap();
        return PInvoke.HeapValidate(heap, 0, null);
    }

    private static HRESULT UpdateOnce(string path, int variant)
    {
        HANDLE h = default;
        fixed (char* p = path)
        {
            HRESULT open = PInvoke.CfOpenFileWithOplock(new PCWSTR(p), CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_NONE, &h);
            if (open.Failed) { return open; }
        }
        try
        {
            if (!PInvoke.CfReferenceProtectedHandle(h)) { return new HRESULT(unchecked((int)0x80004005)); }
            HANDLE win32 = PInvoke.CfGetWin32HandleFromProtectedHandle(h);
            HRESULT hr;
            if (variant == 1)
            {
                long now = DateTime.UtcNow.ToFileTimeUtc();
                var md = new CF_FS_METADATA
                {
                    FileSize = 0,
                    BasicInfo = new FILE_BASIC_INFO { CreationTime = now, LastWriteTime = now, LastAccessTime = now, ChangeTime = now, FileAttributes = (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL },
                };
                byte[] id = Encoding.UTF8.GetBytes("CA2-Identity-" + now);
                fixed (byte* pid = id)
                {
                    hr = PInvoke.CfUpdatePlaceholder(win32, &md, pid, (uint)id.Length, null, 0, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC, null, null);
                }
            }
            else
            {
                hr = PInvoke.CfUpdatePlaceholder(win32, null, null, 0, null, 0, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_NONE, null, null);
            }
            PInvoke.CfReleaseProtectedHandle(h);
            return hr;
        }
        finally
        {
            PInvoke.CfCloseHandle(h);
        }
    }

    private static void Register(string root)
    {
        byte[] identity = Encoding.UTF8.GetBytes("NoDriveHeapRepro");
        fixed (char* pPath = root)
        fixed (char* pName = "NoDrive HeapRepro")
        fixed (char* pVer = "1.0.0")
        fixed (byte* pId = identity)
        {
            var reg = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)sizeof(CF_SYNC_REGISTRATION), ProviderName = new PCWSTR(pName), ProviderVersion = new PCWSTR(pVer),
                SyncRootIdentity = pId, SyncRootIdentityLength = (uint)identity.Length, ProviderId = Guid.Empty,
            };
            var policies = new CF_SYNC_POLICIES
            {
                StructSize = (uint)sizeof(CF_SYNC_POLICIES),
                Hydration = new CF_HYDRATION_POLICY { Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_ALWAYS_FULL, Modifier = CF_HYDRATION_POLICY_MODIFIER.CF_HYDRATION_POLICY_MODIFIER_NONE },
                Population = new CF_POPULATION_POLICY { Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_ALWAYS_FULL, Modifier = CF_POPULATION_POLICY_MODIFIER.CF_POPULATION_POLICY_MODIFIER_NONE },
                InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_NONE,
                HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_NONE,
                PlaceholderManagement = CF_PLACEHOLDER_MANAGEMENT_POLICY.CF_PLACEHOLDER_MANAGEMENT_POLICY_DEFAULT,
            };
            HRESULT hr = PInvoke.CfRegisterSyncRoot(new PCWSTR(pPath), &reg, &policies,
                CF_REGISTER_FLAGS.CF_REGISTER_FLAG_MARK_IN_SYNC_ON_ROOT | CF_REGISTER_FLAGS.CF_REGISTER_FLAG_DISABLE_ON_DEMAND_POPULATION_ON_ROOT | CF_REGISTER_FLAGS.CF_REGISTER_FLAG_UPDATE);
            Marshal.ThrowExceptionForHR(hr.Value);
        }
    }

    private static void CreatePlaceholder(string root, string name)
    {
        if (File.Exists(Path.Combine(root, name))) { return; }
        PlaceholderWriter.CreateChildren(root, new[] { new RemoteItem("CA2-Identity", name, null, false, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) });
    }

    public static void Unregister(string root)
    {
        fixed (char* p = root) { PInvoke.CfUnregisterSyncRoot(new PCWSTR(p)); }
    }
}
