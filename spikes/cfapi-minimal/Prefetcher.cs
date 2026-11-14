using System.Diagnostics;
using NoDrive.Spikes.Scale;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace NoDrive.Spikes.CfApiMinimal;

/// <summary>
/// Populates folders BEFORE the user opens them, so the on-demand FETCH_PLACEHOLDERS (about 160 us per placeholder inside Windows,
/// measured: batch size and parallelism do not help) never blocks Explorer. Creates the children with CfCreatePlaceholders in a
/// low-priority thread, then marks the directory fully populated with CfUpdatePlaceholder(DISABLE_ON_DEMAND_POPULATION).
/// </summary>
internal static class Prefetcher
{
    public static void Start(ItemStore store, string root, string rootId, IReadOnlyList<string> folderIds, int pauseMsBetweenBatches)
    {
        var t = new Thread(() =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                PopulateDirectory(store, root, rootId, pauseMsBetweenBatches);
                foreach (var id in folderIds)
                {
                    string? name = store.GetName(id);
                    if (name is null) { Console.WriteLine($"[prefetch] unknown folder {id}"); continue; }
                    string dir = Path.Combine(root, name);
                    PopulateDirectory(store, dir, id, pauseMsBetweenBatches);
                }
                Console.WriteLine($"[prefetch] done in {sw.Elapsed.TotalSeconds:F1} s");
            }
            catch (Exception ex) { Console.WriteLine($"[prefetch] FAILED {ex.GetType().Name}: {ex.Message}"); }
        })
        { IsBackground = true, Name = "prefetch", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    private static unsafe void PopulateDirectory(ItemStore store, string dir, string folderId, int pauseMs)
    {
        var sw = Stopwatch.StartNew();
        long created = 0;
        var batch = new List<RemoteItem>(1000);
        foreach (var c in store.EnumerateChildren(folderId))
        {
            var when = c.MTime is null ? DateTimeOffset.UnixEpoch : DateTimeOffset.Parse(c.MTime, System.Globalization.CultureInfo.InvariantCulture);
            batch.Add(new RemoteItem(c.Id, c.Name, null, c.IsFolder, c.Size, when, when));
            if (batch.Count == 1000)
            {
                created += PlaceholderWriter.CreateChildren(dir, batch, lazyFolders: true);
                batch.Clear();
                if (pauseMs > 0) { Thread.Sleep(pauseMs); }     // yield so foreground I/O is never starved
            }
        }
        if (batch.Count > 0) { created += PlaceholderWriter.CreateChildren(dir, batch, lazyFolders: true); }

        // Mark the directory fully populated: Windows will not call FETCH_PLACEHOLDERS for it any more.
        HANDLE h = default;
        fixed (char* p = dir)
        {
            HRESULT open = PInvoke.CfOpenFileWithOplock(new PCWSTR(p), CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS, &h);
            if (open.Failed) { Console.WriteLine($"[prefetch] open {dir} -> 0x{open.Value:X8}"); return; }
        }
        try
        {
            HRESULT hr = PInvoke.CfUpdatePlaceholder(h, null, null, 0, null, 0,
                CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DISABLE_ON_DEMAND_POPULATION | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC, null, null);
            Console.WriteLine($"[prefetch] {Path.GetFileName(dir)}: {created} placeholders in {sw.ElapsedMilliseconds} ms; mark populated -> 0x{hr.Value:X8}");
        }
        finally { PInvoke.CfCloseHandle(h); }
    }
}
