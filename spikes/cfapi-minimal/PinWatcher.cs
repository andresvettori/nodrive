using System.Collections.Concurrent;
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace NoDrive.Spikes.CfApiMinimal;

/// <summary>
/// Implements the provider side of "Always keep on this device" / "Free up space". Windows only flips the
/// FILE_ATTRIBUTE_PINNED / UNPINNED bits; the provider must notice and act (NOTIFY_DEHYDRATE is not delivered for
/// Explorer's Free up space, see research/cfapi.md). Pinned online-only file -> CfHydratePlaceholder (which makes
/// the platform call our FETCH_DATA worker); unpinned hydrated file -> CfDehydratePlaceholder.
/// Work is queued and bounded; nothing runs on the watcher thread except attribute reads.
/// </summary>
internal static class PinWatcher
{
    private const FileAttributes Pinned = (FileAttributes)0x00080000;
    private const FileAttributes Unpinned = (FileAttributes)0x00100000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private static FileSystemWatcher? s_watcher;
    private static readonly ConcurrentDictionary<string, byte> s_inFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim s_gate = new(2);

    public static void Start(string root)
    {
        s_watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.Attributes,
            InternalBufferSize = 64 * 1024,
        };
        s_watcher.Changed += (_, e) => Consider(e.FullPath);
        s_watcher.Error += (_, e) => Console.WriteLine($"[pin] watcher error: {e.GetException().Message}");
        s_watcher.EnableRaisingEvents = true;
        Console.WriteLine("[pin] watching attribute changes under " + root);
    }

    public static void Stop() { s_watcher?.Dispose(); s_watcher = null; }

    private static void Consider(string path)
    {
        FileAttributes a;
        try { a = File.GetAttributes(path); } catch { return; }
        if ((a & FileAttributes.Directory) != 0) { return; }

        bool wantHydrate = (a & Pinned) != 0 && (a & RecallOnDataAccess) != 0;
        bool wantDehydrate = (a & Unpinned) != 0 && (a & RecallOnDataAccess) == 0;
        if (!wantHydrate && !wantDehydrate) { return; }
        if (!s_inFlight.TryAdd(path, 0)) { return; }

        _ = Task.Run(async () =>
        {
            await s_gate.WaitAsync();
            try
            {
                var sw = Stopwatch.StartNew();
                string what = wantHydrate ? "hydrate (pinned)" : "dehydrate (unpinned)";
                HRESULT hr = Apply(path, wantHydrate);
                Console.WriteLine($"[pin] {what} {Path.GetFileName(path)} -> 0x{hr.Value:X8} in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[pin] failed {path}: {ex.GetType().Name} {ex.Message}");
            }
            finally
            {
                s_gate.Release();
                s_inFlight.TryRemove(path, out _);
            }
        });
    }

    private static unsafe HRESULT Apply(string path, bool hydrate)
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
        finally
        {
            PInvoke.CfCloseHandle(h);
        }
    }
}
