using System.Collections.Concurrent;
using System.Diagnostics;
using NoDrive.Engine.Infrastructure;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace NoDrive.Engine.CloudFiles;

/// <summary>
/// The provider side of "Always keep on this device" / "Free up space" (plan E3b). Explorer's menu calls CfSetPinState, which flips
/// FILE_ATTRIBUTE_PINNED / UNPINNED but raises NO file-system notification (measured: zero FileSystemWatcher events), so a watcher alone is blind.
/// Detection therefore combines: (1) hints from the platform's "file opened" callback (Explorer must open the file to pin it), checked after a
/// short debounce; (2) a FileSystemWatcher for pins set by other tools (attrib); (3) retries with back-off, because freeing or hydrating a file
/// that is open elsewhere fails with 0x80070187 ("cloud files in use") and must be tried again once it is closed.
/// Action: pinned online-only file -> CfHydratePlaceholder (Windows then calls our FETCH_DATA); unpinned hydrated file -> CfDehydratePlaceholder
/// (refused by Windows when the file is not in sync: intended). At most 2 operations at a time.
/// </summary>
internal static class PinWatcher
{
    private const FileAttributes Pinned = (FileAttributes)0x00080000;
    private const FileAttributes Unpinned = (FileAttributes)0x00100000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const int InUseHr = unchecked((int)0x80070187);
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];

    private static FileSystemWatcher? s_watcher;
    private static readonly ConcurrentDictionary<string, DateTime> Due = new(StringComparer.OrdinalIgnoreCase);       // path -> when to check
    private static readonly ConcurrentDictionary<string, int> Attempts = new(StringComparer.OrdinalIgnoreCase);       // path -> failed attempts so far
    private static readonly ConcurrentDictionary<string, byte> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(2);
    private static CancellationTokenSource? s_cts;

    public static void Start(string root)
    {
        s_cts = new CancellationTokenSource();
        s_watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.Attributes, InternalBufferSize = 64 * 1024 };
        s_watcher.Changed += (_, e) => Hint(e.FullPath, TimeSpan.Zero);
        s_watcher.Error += (_, e) => Log.Warn($"pin watcher error: {e.GetException().Message}");
        s_watcher.EnableRaisingEvents = true;
        _ = Task.Run(() => PumpAsync(s_cts.Token));
    }

    public static void Stop() { s_cts?.Cancel(); s_watcher?.Dispose(); s_watcher = null; }

    /// <summary>Called (cheaply, from callbacks) when something suggests the pin state of <paramref name="path"/> may have changed.</summary>
    public static void Hint(string path, TimeSpan delay)
    {
        if (Due.Count > 20_000) { return; }                                   // bounded: a flood of opens must not grow memory
        Due.AddOrUpdate(path, DateTime.UtcNow + delay, (_, old) => old);
    }

    private static async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(400, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            if (Due.IsEmpty) { continue; }
            var now = DateTime.UtcNow;
            foreach (var kv in Due)
            {
                if (kv.Value > now) { continue; }
                if (Due.TryRemove(kv.Key, out _)) { Consider(kv.Key); }
            }
        }
    }

    private static void Consider(string path)
    {
        FileAttributes a;
        try { a = File.GetAttributes(path); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
        if ((a & FileAttributes.Directory) != 0) { return; }
        bool hydrate = (a & Pinned) != 0 && (a & RecallOnDataAccess) != 0;
        bool dehydrate = (a & Unpinned) != 0 && (a & RecallOnDataAccess) == 0;
        if (!hydrate && !dehydrate) { Attempts.TryRemove(path, out _); return; }
        if (!InFlight.TryAdd(path, 0)) { return; }

        _ = Task.Run(async () =>
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var sw = Stopwatch.StartNew();
                HRESULT hr = Apply(path, hydrate);
                string what = hydrate ? "keep on device" : "free up space";
                if (hr.Succeeded) { Attempts.TryRemove(path, out _); NotifyShell(path); Log.Info($"{what}: {Path.GetFileName(path)} done in {sw.ElapsedMilliseconds} ms"); }
                else if (hr.Value == InUseHr)
                {
                    int n = Attempts.AddOrUpdate(path, 1, (_, c) => c + 1);
                    if (n <= RetryDelays.Length) { Log.Info($"{what}: {Path.GetFileName(path)} is in use; retry {n}/{RetryDelays.Length} in {RetryDelays[n - 1].TotalSeconds:F0} s"); Hint(path, RetryDelays[n - 1]); }
                    else { Log.Warn($"{what}: {Path.GetFileName(path)} stayed in use; giving up until its pin state changes again"); Attempts.TryRemove(path, out _); }
                }
                else if (hr.Value == unchecked((int)0x80070179)) { Log.Info($"{what}: {Path.GetFileName(path)} is not in sync; Windows refuses (intended)"); }
                else { Log.Warn($"{what}: {Path.GetFileName(path)} -> 0x{hr.Value:X8}"); }
            }
            catch (Exception ex) { Log.Warn($"pin action failed for {path}: {ex.Message}"); }
            finally { Gate.Release(); InFlight.TryRemove(path, out _); }
        });
    }

    /// <summary>Tells Explorer the item changed so the icon refreshes without F5.</summary>
    internal static unsafe void NotifyShell(string path)
    {
        fixed (char* p = path) { PInvoke.SHChangeNotify(Windows.Win32.UI.Shell.SHCNE_ID.SHCNE_UPDATEITEM, Windows.Win32.UI.Shell.SHCNF_FLAGS.SHCNF_PATHW | Windows.Win32.UI.Shell.SHCNF_FLAGS.SHCNF_FLUSHNOWAIT, p, null); }
    }

    internal static unsafe HRESULT Apply(string path, bool hydrate)
    {
        HANDLE h = default;
        fixed (char* p = path)
        {
            HRESULT open = PInvoke.CfOpenFileWithOplock(new PCWSTR(p), CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS, &h);
            if (open.Failed) { return open; }
        }
        try
        {
            return hydrate
                ? PInvoke.CfHydratePlaceholder(h, 0, -1, CF_HYDRATE_FLAGS.CF_HYDRATE_FLAG_NONE, null)
                : PInvoke.CfDehydratePlaceholder(h, 0, -1, CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE, null);
        }
        finally { PInvoke.CfCloseHandle(h); }
    }
}
