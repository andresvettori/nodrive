using System.Collections.Concurrent;
using System.Diagnostics;
using NoDrive.Engine.Infrastructure;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace NoDrive.Engine.CloudFiles;

/// <summary>
/// The provider side of "Always keep on this device" / "Free up space" (plan E3b). Explorer only flips FILE_ATTRIBUTE_PINNED / UNPINNED;
/// the provider must notice and act: pinned online-only file -> CfHydratePlaceholder (Windows then calls our FETCH_DATA); unpinned hydrated
/// file -> CfDehydratePlaceholder (refused by Windows when the file is not in sync, which is the safety rule we want).
/// Bounded to 2 concurrent operations; nothing runs on the watcher thread except attribute reads.
/// </summary>
internal static class PinWatcher
{
    private const FileAttributes Pinned = (FileAttributes)0x00080000;
    private const FileAttributes Unpinned = (FileAttributes)0x00100000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private static FileSystemWatcher? s_watcher;
    private static readonly ConcurrentDictionary<string, byte> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(2);

    public static void Start(string root)
    {
        s_watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.Attributes, InternalBufferSize = 64 * 1024 };
        s_watcher.Changed += (_, e) => Consider(e.FullPath);
        s_watcher.Error += (_, e) => Log.Warn($"pin watcher error: {e.GetException().Message}");
        s_watcher.EnableRaisingEvents = true;
    }

    public static void Stop() { s_watcher?.Dispose(); s_watcher = null; }

    private static void Consider(string path)
    {
        FileAttributes a;
        try { a = File.GetAttributes(path); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
        if ((a & FileAttributes.Directory) != 0) { return; }
        bool hydrate = (a & Pinned) != 0 && (a & RecallOnDataAccess) != 0;
        bool dehydrate = (a & Unpinned) != 0 && (a & RecallOnDataAccess) == 0;
        if (!hydrate && !dehydrate) { return; }
        if (!InFlight.TryAdd(path, 0)) { return; }

        _ = Task.Run(async () =>
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var sw = Stopwatch.StartNew();
                HRESULT hr = Apply(path, hydrate);
                // 0x80070179 (not in sync): Windows refuses to dehydrate a file with local changes; that is intended, not an error.
                Log.Info($"{(hydrate ? "keep on device" : "free up space")}: {Path.GetFileName(path)} -> 0x{hr.Value:X8} in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex) { Log.Warn($"pin action failed for {path}: {ex.Message}"); }
            finally { Gate.Release(); InFlight.TryRemove(path, out _); }
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
        finally { PInvoke.CfCloseHandle(h); }
    }
}
