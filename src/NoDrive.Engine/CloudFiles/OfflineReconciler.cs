using NoDrive.Core.Index;
using NoDrive.Core.Model;
using NoDrive.Core.Names;
using NoDrive.Core.Sync;
using NoDrive.Engine.Infrastructure;

namespace NoDrive.Engine.CloudFiles;

/// <summary>
/// Catches up with what happened on disk while NoDrive was not running (crash, stop, reboot, update): edited downloads, plain files and folders, renamed or moved placeholders and deleted items.
/// Only items known to exist on disk (index flag <c>local</c>) can be judged deleted, names that differ only by case or cannot exist on Windows are never judged, and if suspiciously many items are
/// missing (a wiped folder, a restored backup, a moved sync root) nothing is deleted in the cloud: the list is held for the user to restore or confirm.
/// Runs after the first cloud sync of a start, then every few hours or on request; local I/O only, yields regularly.
/// </summary>
internal sealed class OfflineReconciler(ItemIndex index, PopulationState population, string syncRoot) : IDisposable
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000, Pinned = (FileAttributes)0x80000;
    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly SemaphoreSlim _poke = new(0);
    private List<(string Id, string Name, string FolderId)> _held = [];

    /// <summary>Items missing on disk that were not deleted in the cloud because there were too many (needs the user's decision).</summary>
    public int HeldCount => _held.Count;
    public DateTime? LastRunUtc { get; private set; }
    public string LastSummary { get; private set; } = "";

    /// <summary>Folders examined so far and in total while a run is in progress (both 0 when idle); shown on the page so the start-up check is not silent.</summary>
    public int ProgressDone => Volatile.Read(ref _done);
    public int ProgressTotal => Volatile.Read(ref _total);
    private int _done, _total;

    public void RequestRun() => _poke.Release();

    public void Dispose() { _run.Dispose(); _poke.Dispose(); }

    public async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                try { await RunAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { Log.Warn($"reconcile failed: {ex.Message}"); }
                await _poke.WaitAsync(TimeSpan.FromHours(6), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Runs the check on its own below-normal-priority thread: the walk is CPU-bound (thousands of folders), and at normal priority it competed with the user's programs for a
    /// couple of minutes. Memory is compacted afterwards so the burst does not leave the process larger than before.
    /// </summary>
    public Task RunAsync(CancellationToken ct) => Task.Factory.StartNew(() => RunCore(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void RunCore(CancellationToken ct)
    {
        if (!_run.Wait(0, ct)) { return; }
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int folders = 0, edited = 0, queuedPlain = 0, renamed = 0;
            var missing = new List<(string Id, string Name, string FolderId)>();
            var unknownPlaceholders = new List<string>();
            var plain = new List<string>();
            var folderIds = population.PopulatedSet().ToList();
            Volatile.Write(ref _done, 0); Volatile.Write(ref _total, folderIds.Count);
            foreach (string folderId in folderIds)
            {
                Volatile.Write(ref _done, folders);
                ct.ThrowIfCancellationRequested();
                string? dir = population.PathOfFolder(folderId, syncRoot);
                if (dir is null || !Directory.Exists(dir)) { continue; }
                var disk = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
                try { foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos()) { disk[e.Name] = e; } }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }       // cannot read it now: judge nothing

                var all = index.EnumerateChildren(folderId).ToList();
                var counts = all.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var known = all.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var c in all.Where(c => c.Local))
                {
                    if (disk.TryGetValue(c.Name, out var info))
                    {
                        if (!c.IsFolder && (info.Attributes & (Pinned | RecallOnDataAccess)) == (Pinned | RecallOnDataAccess)) { PinWatcher.Hint(info.FullName, TimeSpan.FromSeconds(2)); }          // pinned ("always keep") but not downloaded: finish it
                        if ((info.Attributes & FileAttributes.ReparsePoint) == 0) { plain.Add(info.FullName); }
                        else if (!c.IsFolder && info is FileInfo fi && (fi.Attributes & (FileAttributes.Offline | RecallOnDataAccess)) == 0 && (c.Size != fi.Length || Math.Abs(c.ModifiedUtcTicks - fi.LastWriteTimeUtc.Ticks) > 2 * TimeSpan.TicksPerSecond))
                        {
                            plain.Add(fi.FullName); edited++;           // an edited download: the upload service decides (update, or conflict copy)
                        }
                    }
                    else if (!counts.Contains(c.Name) && WindowsNames.IsValid(c.Name)) { missing.Add((c.Id, c.Name, folderId)); }
                }
                foreach (var (name, info) in disk)
                {
                    if (known.Contains(name)) { continue; }
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { unknownPlaceholders.Add(info.FullName); }
                    else { plain.Add(info.FullName); }
                }
                if (++folders % 50 == 0) { Thread.Sleep(20); }
                // Hand the changes found so far to the upload queue right away instead of waiting for the end of the walk: the first change leaves in seconds, not minutes.
                if (plain.Count > 0 && (plain.Count >= 50 || folders % 100 == 0)) { queuedPlain += Flush(plain); }
            }

            // Placeholders that are not where the index expects them were renamed or moved while we were stopped: replay as a rename (the identity inside the placeholder is the cloud id).
            foreach (string path in unknownPlaceholders)
            {
                ct.ThrowIfCancellationRequested();
                if (PlaceholderInfo.TryReadIdentity(path) is not { Length: > 0 } id || index.TryGet(id) is not { } item) { continue; }
                missing.RemoveAll(m => m.Id == id);
                LocalOps.Post("rename", id, path);
                renamed++;
            }

            queuedPlain += Flush(plain);

            long local = index.CountLocal();
            long limit = MissingGuard.Limit(local);
            if (MissingGuard.ShouldHold(missing.Count, local))
            {
                _held = missing;
                Log.Warn($"reconcile: {missing.Count} items are missing on this PC (limit {limit}); nothing was deleted in the cloud");
                ActivityLog.Add("error", "NoDrive", "skipped", detail: $"{missing.Count} items are missing on this PC; they were not deleted in the cloud. Open the activity page to restore or confirm.");
            }
            else
            {
                _held = [];
                foreach (var m in missing) { LocalOps.Post("delete", m.Id, null); }
            }
            LastRunUtc = DateTime.UtcNow;
            LastSummary = $"{folders} folders checked, {edited} edited, {queuedPlain} plain, {renamed} moved, {missing.Count} missing{(_held.Count > 0 ? " (held)" : "")}, {sw.ElapsedMilliseconds} ms";
            Log.Info("reconcile: " + LastSummary);
            if (edited + queuedPlain + renamed > 0 || (missing.Count > 0 && _held.Count == 0)) { ActivityLog.Add("sync", "NoDrive", "ok", detail: "caught up with changes made while it was stopped: " + LastSummary); }
        }
        finally { Volatile.Write(ref _total, 0); Volatile.Write(ref _done, 0); _run.Release(); GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true); }
    }

    /// <summary>Queues the collected paths for upload (skipping names that are never uploaded), empties the list and returns how many were queued.</summary>
    private int Flush(List<string> plain)
    {
        var send = plain.Where(p => !OneDriveNames.IsNeverUploaded(Path.GetFileName(p), string.Equals(Path.GetDirectoryName(p), syncRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))).ToList();
        int n = plain.Count;
        plain.Clear();
        if (send.Count > 0) { index.EnqueueUploads(send, DateTime.UtcNow.AddSeconds(5).Ticks); }
        return n;
    }

    /// <summary>The user says the missing items should come back: forget they were deleted and let Windows fill the folders in from the cloud again.</summary>
    public int RestoreHeld()
    {
        var list = _held;
        _held = [];
        int n = 0;
        foreach (var folderId in list.Select(m => m.FolderId).Distinct())
        {
            string? dir = population.PathOfFolder(folderId, syncRoot);
            population.Set(folderId, false);
            if (dir is not null && Directory.Exists(dir)) { PlaceholderWriter.EnableOnDemandPopulation(dir); n++; }
        }
        ActivityLog.Add("sync", "NoDrive", "ok", detail: $"restoring {list.Count} missing items from the cloud (open the folders to see them)");
        return list.Count;
    }

    /// <summary>The user says the missing items really were deleted: delete them in the cloud too (recycle bin), still through the normal delete guard.</summary>
    public int ConfirmDeleteHeld()
    {
        var list = _held;
        _held = [];
        foreach (var m in list) { LocalOps.Post("delete", m.Id, null); }
        ActivityLog.Add("delete", "NoDrive", "ok", detail: $"{list.Count} items confirmed deleted");
        return list.Count;
    }
}
