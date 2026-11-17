using NoDrive.Core.Index;
using NoDrive.Core.Model;
using NoDrive.Core.Names;
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

    public void RequestRun() => _poke.Release();

    public void Dispose() { _run.Dispose(); _poke.Dispose(); }

    public async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
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

    public async Task RunAsync(CancellationToken ct)
    {
        if (!await _run.WaitAsync(0, ct).ConfigureAwait(false)) { return; }
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int folders = 0, edited = 0, queuedPlain = 0, renamed = 0;
            var missing = new List<(string Id, string Name, string FolderId)>();
            var unknownPlaceholders = new List<string>();
            var plain = new List<string>();
            foreach (string folderId in population.PopulatedSet())
            {
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
                if (++folders % 50 == 0) { await Task.Delay(20, ct).ConfigureAwait(false); }
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

            if (plain.Count > 0) { index.EnqueueUploads(plain.Where(p => !ShouldIgnore(p)), DateTime.UtcNow.AddSeconds(5).Ticks); queuedPlain = plain.Count; }

            long local = index.CountLocal();
            long limit = Math.Max(50, local / 20);
            if (missing.Count > limit)
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
        finally { _run.Release(); }
    }

    private static bool ShouldIgnore(string path)
    {
        string n = Path.GetFileName(path);
        return n.StartsWith("~$", StringComparison.Ordinal) || n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || n.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || n.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);
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
