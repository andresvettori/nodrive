using NoDrive.Core.Sync;
using NoDrive.Core.Hashing;
using System.Collections.Concurrent;
using System.Net;
using NoDrive.Core.Index;
using NoDrive.Core.Model;
using NoDrive.Engine.Infrastructure;
using NoDrive.Graph;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;

namespace NoDrive.Engine.CloudFiles;

/// <summary>
/// Uploads NEW local files and folders (E5). Never inside a cfapi callback: a watcher only records paths; a worker loop does the I/O.
/// Journal first (SQLite uploads table), debounce until a file has been quiet for a few seconds, at most two uploads at once,
/// conflictBehavior=fail with a conflict-copy name on a clash, and the file becomes an in-sync placeholder only after the server confirmed it.
/// Never overwrites or deletes remote data. Existing (placeholder) files are ignored: in-place edits are a later phase.
/// </summary>
internal sealed class UploadService(ItemIndex index, GraphDrive graph, PopulationState population, string syncRoot, string rootId) : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];
    private const int MaxWatched = 100_000;
    private const int Lanes = 2;

    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private volatile bool _rescan;
    private readonly Dictionary<string, Task> _active = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of queued uploads (for the status line).</summary>
    public long Pending => index.CountUploads();

    public void Start(CancellationToken ct)
    {
        _watcher = new FileSystemWatcher(syncRoot) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 64 * 1024 };
        _watcher.Created += (_, e) => Note(e.FullPath);
        _watcher.Renamed += (_, e) => Note(e.FullPath);
        _watcher.Changed += (_, e) => NoteChange(e.FullPath);          // edits of downloaded files (the predicate runs later in FlushSeen, not on the event thread)
        _watcher.Error += (_, e) => { Log.Warn($"upload watcher overflow: {e.GetException().Message}; rescanning populated folders"); _rescan = true; };
        _watcher.EnableRaisingEvents = true;
        _ = Task.Run(() => LoopAsync(ct), CancellationToken.None);
    }

    public void Dispose() => _watcher?.Dispose();

    private void NoteChange(string path)
    {
        if (_seen.Count < MaxWatched && !Ignored(path) && !_seen.ContainsKey(path)) { _seen.TryAdd(path, 0); }
    }

    private void Note(string path)
    {
        if (_seen.Count < MaxWatched && !Ignored(path)) { _seen.TryAdd(path, 0); }
        else if (_seen.Count >= MaxWatched) { _rescan = true; }
    }

    private bool Ignored(string path) =>
        NoDrive.Core.Names.OneDriveNames.IsNeverUploaded(Path.GetFileName(path), string.Equals(Path.GetDirectoryName(path), syncRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    private static bool IsRealLocal(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
        catch (Exception) { return false; }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            await ScanPopulatedAsync(ct).ConfigureAwait(false);                       // files added while the engine was off
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (UserSettings.Paused) { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); continue; }
                    if (index.GetMeta("adopt.pending") == "1" && index.CountUploads() == 0 && _active.Count == 0) { index.SetMeta("adopt.pending", null); ActivityLog.Add("sync", "NoDrive", "ok", detail: "folder linked"); Log.Info("adoption finished"); }
                    if (_rescan) { _rescan = false; await ScanPopulatedAsync(ct).ConfigureAwait(false); }
                    FlushSeen();
                    foreach (var done in _active.Where(kv => kv.Value.IsCompleted).Select(kv => kv.Key).ToList()) { _active.Remove(done); }
                    if (_active.Count < Lanes)
                    {
                        // A long upload occupies one lane only; the other lane keeps taking due work.
                        foreach (var j in index.DueUploads(DateTime.UtcNow.Ticks, Lanes + _active.Count))
                        {
                            if (_active.Count >= Lanes) { break; }
                            if (!_active.ContainsKey(j.Path)) { _active[j.Path] = ProcessAsync(j.Path, j.Attempts, ct); }
                        }
                    }
                    // Wake as soon as any running job finishes: linking an existing file takes milliseconds, a fixed pause would cap the rate at one item per second.
                    if (_active.Count == 0) { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
                    else { await Task.WhenAny(_active.Values.Append(Task.Delay(TimeSpan.FromSeconds(1), ct))).ConfigureAwait(false); }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { Log.Warn($"upload loop: {ex.Message}"); await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void FlushSeen()
    {
        if (_seen.IsEmpty) { return; }
        var batch = new List<string>();
        foreach (var k in _seen.Keys) { if (_seen.TryRemove(k, out _) && (IsRealLocal(k) || IsEditedPlaceholder(k))) { batch.Add(k); } }
        if (batch.Count > 0) { index.EnqueueUploads(batch, DateTime.UtcNow.Add(Quiet).Ticks); }
    }

    /// <summary>Queues real (non-placeholder) entries found in folders that exist on disk. Local I/O only, yields regularly.</summary>
    private async Task ScanPopulatedAsync(CancellationToken ct)
    {
        int n = 0, queued = 0;
        if (index.GetMeta("adopt.pending") == "1" || index.GetMeta("repair.v2") != "1")
        {
            // Adoption / repair walk: every plain entry under materialised folders is queued for linking, and every materialised folder is told it still has cloud children.
            var (q, fixedDirs) = await WalkAndRepairAsync(syncRoot, ct).ConfigureAwait(false);
            queued += q;
            index.SetMeta("repair.v2", "1");
            Log.Info($"adoption walk: {q} entries queued for linking, {fixedDirs} folders set to fill in from the cloud");
        }
        foreach (var id in population.PopulatedSet())
        {
            ct.ThrowIfCancellationRequested();
            string? dir = id == rootId ? syncRoot : PathOfFolder(id);
            if (dir is null || !Directory.Exists(dir)) { continue; }
            var found = new List<string>();
            try { foreach (var e in Directory.EnumerateFileSystemEntries(dir)) { if (!Ignored(e) && IsRealLocal(e)) { found.Add(e); } } }
            catch (Exception) { continue; }
            if (found.Count > 0) { index.EnqueueUploads(found, DateTime.UtcNow.Add(Quiet).Ticks); queued += found.Count; }
            if (++n % 200 == 0) { await Task.Delay(50, ct).ConfigureAwait(false); }
        }
        if (queued > 0) { Log.Info($"upload scan: {queued} local item(s) queued"); }
    }

    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000;

    /// <summary>
    /// Walks the sync root through folders that exist on disk (never into online-only folders: that would make Windows populate them). Plain entries are queued for
    /// linking; placeholder folders get on-demand population enabled so cloud files that were never downloaded show up when they are opened.
    /// </summary>
    private async Task<(int Queued, int Fixed)> WalkAndRepairAsync(string root, CancellationToken ct)
    {
        int queued = 0, fixedDirs = 0, visited = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        var batch = new List<string>();
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = pending.Pop();
            try
            {
                foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    var a = e.Attributes;
                    bool reparse = (a & FileAttributes.ReparsePoint) != 0;
                    if (!reparse) { if (!Ignored(e.FullName)) { batch.Add(e.FullName); } continue; }       // plain file or folder: link it (children are queued once the folder is linked)
                    if ((a & FileAttributes.Directory) == 0 || (a & (RecallOnOpen | RecallOnDataAccess)) != 0) { continue; }   // placeholder file, or an online-only folder
                    if (PlaceholderWriter.EnableOnDemandPopulation(e.FullName)) { fixedDirs++; }
                    pending.Push(e.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"adoption walk skipped {dir}: {ex.Message}"); }
            if (batch.Count >= 500) { index.EnqueueUploads(batch, DateTime.UtcNow.Ticks); queued += batch.Count; batch.Clear(); }
            if (++visited % 100 == 0) { await Task.Delay(20, ct).ConfigureAwait(false); }
        }
        if (batch.Count > 0) { index.EnqueueUploads(batch, DateTime.UtcNow.Ticks); queued += batch.Count; }
        return (queued, fixedDirs);
    }

    private string? PathOfFolder(string id)
    {
        var chain = index.PathTo(id, rootId);
        var parts = new string[chain.Count + 1];
        parts[0] = syncRoot;
        for (int i = 0; i < chain.Count; i++) { if (index.GetName(chain[i]) is not { } nm) { return null; } parts[i + 1] = nm; }
        return Path.Combine(parts);
    }

    /// <summary>Resolves the remote id of the folder holding <paramref name="path"/> by walking the index; null when an ancestor is not uploaded yet.</summary>
    private string? ResolveParent(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is null) { return null; }
        string rel = Path.GetRelativePath(syncRoot, dir);
        if (rel == ".") { return rootId; }
        string current = rootId;
        foreach (var seg in rel.Split(Path.DirectorySeparatorChar))
        {
            var child = index.FindChild(current, seg);
            if (child is not { IsFolder: true } c) { return null; }
            current = c.Id;
        }
        return current;
    }

    private async Task ProcessAsync(string path, int attempts, CancellationToken ct)
    {
        ActiveTransfer? act = null;
        try
        {
            bool isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path)) { index.RemoveUpload(path); return; }                                 // gone
            if (!IsRealLocal(path)) { if (isDir) { index.RemoveUpload(path); } else { await ProcessEditAsync(path, attempts, ct).ConfigureAwait(false); } return; }     // a placeholder: only an edit matters
            string? parentId = ResolveParent(path);
            if (parentId is null) { Defer(path, attempts, TimeSpan.FromSeconds(15), bump: false); return; }       // the parent folder is still queued: wait for it

            string name = Path.GetFileName(path);
            RemoteItem item;
            bool adopted = false;                      // the cloud already has this exact item: link, do not upload
            string finalPath = path;
            DateTime stamp = isDir ? default : File.GetLastWriteTimeUtc(path);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (isDir)
            {
                if (index.FindChild(parentId, name) is { } existing)
                {
                    if (!existing.IsFolder) { Defer(path, attempts, TimeSpan.FromMinutes(1), bump: true); return; }        // a cloud FILE has this name: never merge a folder into it
                    item = ToRemote(existing);             // same folder in the cloud (for example after a reinstall): link it and let its files be matched one by one
                    adopted = true;
                }
                else { item = await graph.CreateFolderAsync(parentId, name, ct).ConfigureAwait(false); }
            }
            else
            {
                try { using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
                catch (IOException) { Defer(path, attempts, TimeSpan.FromSeconds(10), bump: false); return; }          // still being written
                IndexedItem? twin = index.FindChild(parentId, name) is { IsFolder: false } f ? f : null;
                if (twin is { } same && SameFile(same, path))
                {
                    item = ToRemote(same);                 // already in the cloud with the same size and modified time (a file NoDrive downloaded earlier, or one OneDrive synced): link it
                    adopted = true;
                }
                else
                {
                // A plain file whose name exists in the cloud is a replace: a safe-save (the delete of the original is still waiting) or a copy that is newer than the cloud version.
                bool replaceExisting = twin is { } t0 && (index.HasPendingOp(t0.Id, "delete") || File.GetLastWriteTimeUtc(path).Ticks > t0.ModifiedUtcTicks + 2 * TimeSpan.TicksPerSecond);
                act = ActivityLog.Begin(replaceExisting ? "update" : "upload", name, new FileInfo(path).Length);
                try
                {
                    if (replaceExisting)
                    {
                        item = await graph.UpdateFileAsync(twin!.Value.Id, twin.Value.ETag, path, ct, ProgressLogger(name, act)).ConfigureAwait(false);
                        index.RemoveOps(twin.Value.Id, "delete");
                        File.SetLastWriteTimeUtc(path, item.ModifiedUtc.UtcDateTime);
                        stamp = File.GetLastWriteTimeUtc(path);
                    }
                    else { item = await graph.UploadNewFileAsync(parentId, name, path, ct, ProgressLogger(name, act)).ConfigureAwait(false); }
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                {
                    if (twin is { } t1) { index.RemoveOps(t1.Id, "delete"); }       // keep the cloud original when the replace cannot be done safely
                    // A timeout can hide an upload that actually finished: if the cloud file with this name has our exact content, it IS our file (link it, no copy).
                    var theirs = await graph.GetChildByNameAsync(parentId, name, ct).ConfigureAwait(false);
                    Log.Info($"name clash on '{name}' (attempt {attempts}): cloud has {(theirs is null ? "nothing visible" : $"{theirs.Size:N0} bytes, hash {theirs.QuickXorHash ?? "none"}, modified {theirs.ModifiedUtc:u}")}; local is {new FileInfo(path).Length:N0} bytes");
                    long localSize = new FileInfo(path).Length;
                    var action = ClashPolicy.Decide(localSize, theirs is not null, theirs?.IsFolder ?? false, theirs?.Size ?? 0, theirs?.QuickXorHash, attempts, () => QuickXorHash.OfFile(path, ct));
                    if (action == ClashAction.ClearEmptyLeftover && !await TryRemoveEmptyGhostAsync(theirs!, name, ct).ConfigureAwait(false))
                    {
                        action = ClashPolicy.Decide(localSize, true, false, theirs!.Size, theirs.QuickXorHash, attempts, () => QuickXorHash.OfFile(path, ct), leftoverRemovalFailed: true);
                    }
                    switch (action)
                    {
                        case ClashAction.LinkExisting:
                            item = theirs!;
                            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path));
                            Log.Info($"'{name}' was already in the cloud with the same content (an earlier attempt finished): linked, no conflict copy");
                            break;
                        case ClashAction.ClearEmptyLeftover:
                            // An empty cloud file with our name that our index never saw is the leftover of an upload session we abandoned (OneDrive keeps it for hours): it was removed, try again.
                            ActivityLog.End(act!, "failed", "retrying after clearing an interrupted upload"); act = null;
                            Defer(path, attempts, TimeSpan.FromSeconds(3), bump: true);
                            return;
                        case ClashAction.WaitForReservedName:
                            Log.Info($"'{name}': the name is still reserved by an interrupted upload; retrying later");
                            ActivityLog.End(act!, "failed", "waiting for an interrupted upload to expire"); act = null;
                            Defer(path, attempts, TimeSpan.FromMinutes(2), bump: true);
                            return;
                        default:
                        {
                            string copy = ConflictName(name);
                            item = await graph.UploadNewFileAsync(parentId, copy, path, ct, ProgressLogger(copy, act)).ConfigureAwait(false);
                            finalPath = Path.Combine(Path.GetDirectoryName(path)!, copy);
                            File.Move(path, finalPath);
                            Log.Warn($"name clash for '{name}': uploaded as '{copy}'");
                            break;
                        }
                    }
                }
                if (File.Exists(finalPath) && File.GetLastWriteTimeUtc(finalPath) != stamp) { Log.Warn($"{name} changed while uploading; the uploaded copy is the earlier version and the file stays a plain local file"); index.RemoveUpload(path); ActivityLog.End(act!, "skipped", "changed while uploading"); act = null; return; }
                }
            }

            if (!adopted) { index.ApplyPage([DeltaChange.Upsert(item)], "upload.last", item.Id, index.Generation); }
            bool converted = ConvertToPlaceholder(finalPath, item.Id, onDemand: isDir && adopted);
            if (!converted)
            {
                // Usually a sharing violation (the item is open in Explorer). The cloud side is done and in the index: retry the link later, never forget it.
                if (act is not null) { ActivityLog.End(act, "ok", "uploaded; linking later"); act = null; }
                Defer(path, attempts, TimeSpan.FromSeconds(30), bump: true);
                return;
            }
            index.MarkLocal(item.Id);
            if (!isDir && !adopted) { VerifyHashSample(finalPath, item); }
            if (isDir) { if (!adopted) { population.Set(item.Id, true); } QueueChildren(finalPath); }
            index.RemoveUpload(path);
            if (act is not null) { ActivityLog.End(act, "ok", finalPath != path ? "conflict copy" : null); act = null; }
            else if (adopted) { ActivityLog.Add("sync", Path.GetFileName(finalPath), "ok", detail: "already in the cloud: linked"); }
            else { ActivityLog.Add(isDir ? "create" : "upload", Path.GetFileName(finalPath), "ok", 0, sw.ElapsedMilliseconds); }
            PinWatcher.NotifyShell(finalPath);
            if (Path.GetDirectoryName(finalPath) is { } parentDir) { RemoteApplier.NotifyDir(parentDir); }
            Log.Info($"{(adopted ? "linked" : "uploaded")} {(isDir ? "folder" : "file")} '{Path.GetFileName(finalPath)}' in {sw.ElapsedMilliseconds} ms{(converted ? "" : " (placeholder conversion failed, will show as local until the next sync)")}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.InsufficientStorage or HttpStatusCode.NotFound)
        {
            if (act is not null) { ActivityLog.End(act, "failed", ex.Message); act = null; } else { ActivityLog.Add("error", Path.GetFileName(path), "failed", detail: ex.Message); }
            Log.Warn($"upload of '{Path.GetFileName(path)}' refused ({ex.Message}); retrying in 6 h");
            Defer(path, attempts, TimeSpan.FromHours(6), bump: true);
        }
        catch (Exception ex)
        {
            var wait = Backoff[Math.Min(attempts, Backoff.Length - 1)];
            if (act is not null) { ActivityLog.End(act, "failed", ex.Message); act = null; } else { ActivityLog.Add("error", Path.GetFileName(path), "failed", detail: ex.Message); }
            Log.Warn($"upload of '{Path.GetFileName(path)}' failed ({ex.Message}); retry in {wait.TotalSeconds:F0} s");
            Defer(path, attempts, wait, bump: true);
        }
    }

    /// <summary>Logs large uploads about every 10%; small files stay silent.</summary>
    private static Action<long, long> ProgressLogger(string name, ActiveTransfer? act)
    {
        int last = 0;
        return (sent, total) =>
        {
            if (act is not null) { act.Done = sent; }
            if (total < 20 * 1024 * 1024) { return; }
            int pct = (int)(sent * 100 / total) / 10 * 10;
            if (pct > last && pct < 100) { last = pct; Log.Info($"uploading '{name}': {pct}% ({sent / 1048576} of {total / 1048576} MiB)"); }
        };
    }

    private static int s_hashChecks;

    /// <summary>
    /// Diagnostic only (never changes behaviour): for the first uploads of each run, compare OneDrive's quickXorHash with ours, so a wrong implementation shows up in the log
    /// instead of silently disabling the "identical content" shortcuts. Bounded: 50 files per run, up to 256 MiB each.
    /// </summary>
    private static void VerifyHashSample(string path, RemoteItem item)
    {
        try
        {
            if (item.QuickXorHash is not { } cloud || new FileInfo(path).Length is > 256L * 1024 * 1024 || System.Threading.Interlocked.Increment(ref s_hashChecks) > 50) { return; }
            string local = QuickXorHash.OfFile(path);
            if (local == cloud) { Log.Info($"hash check ok for '{Path.GetFileName(path)}'"); }
            else { Log.Warn($"hash check MISMATCH for '{Path.GetFileName(path)}': cloud {cloud}, local {local}"); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Deletes (recycle bin, If-Match) the empty leftover of an interrupted upload session so the name is free again. False when it could not be removed.</summary>
    private async Task<bool> TryRemoveEmptyGhostAsync(RemoteItem ghost, string name, CancellationToken ct)
    {
        try { await graph.DeleteItemAsync(ghost.Id, ghost.ETag, ct).ConfigureAwait(false); Log.Info($"removed the empty leftover of an interrupted upload of '{name}'"); return true; }
        catch (HttpRequestException ex) { Log.Warn($"could not remove the empty leftover of '{name}': {ex.Message}"); return false; }
    }

    private void Defer(string path, int attempts, TimeSpan wait, bool bump) => index.RescheduleUpload(path, bump ? attempts + 1 : attempts, DateTime.UtcNow.Add(wait).Ticks);

    private static bool IsEdited(IndexedItem item, FileInfo fi) => ChangeDetection.IsEdited(item.Size, item.ModifiedUtcTicks, fi.Length, fi.LastWriteTimeUtc.Ticks);

    /// <summary>A downloaded placeholder file whose size or modified time no longer match the cloud version we know: the user edited it.</summary>
    private bool IsEditedPlaceholder(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || (fi.Attributes & FileAttributes.ReparsePoint) == 0 || (fi.Attributes & (FileAttributes.Offline | RecallOnDataAccess)) != 0) { return false; }
            string? parentId = ResolveParent(path);
            return parentId is not null && index.FindChild(parentId, fi.Name) is { IsFolder: false } cur && IsEdited(cur, fi);
        }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// Uploads the edit of a downloaded file: If-Match with the eTag we know; on a version conflict (412) the user's version becomes a conflict copy and the original shows the cloud version,
    /// so nothing is lost on either side. Afterwards the placeholder takes the cloud's new time and size so the edit is not seen twice.
    /// </summary>
    private async Task ProcessEditAsync(string path, int attempts, CancellationToken ct)
    {
        ActiveTransfer? act = null;
        try
        {
            var fi = new FileInfo(path);
            string? parentId = ResolveParent(path);
            if (parentId is null || index.FindChild(parentId, fi.Name) is not { IsFolder: false } cur || (fi.Attributes & (FileAttributes.Offline | RecallOnDataAccess)) != 0 || !IsEdited(cur, fi)) { index.RemoveUpload(path); return; }
            try { using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (IOException) { Defer(path, attempts, TimeSpan.FromSeconds(10), bump: false); return; }          // still open for writing (Office keeps it open while editing)

            // Same content as the cloud copy (a "touch", a save without changes, a time-only difference): do not upload, just mark it in sync. The hash is only computed when the
            // size is equal and the file is not huge; a different hash (or our hash being wrong) simply falls through to the normal upload.
            if (ChangeDetection.LocalEqualsCloud(cur.Size, cur.QuickXorHash, fi.Length, () => QuickXorHash.OfFile(path, ct)))
            {
                if (!PlaceholderWriter.RefreshInSync(path, ToRemote(cur)) && !RemoteApplier.MarkInSync(path)) { ConvertToPlaceholder(path, cur.Id, onDemand: false); }
                index.RemoveUpload(path);
                PinWatcher.NotifyShell(path);
                Log.Info($"'{fi.Name}' has the same content as the cloud copy: marked in sync, nothing uploaded");
                return;
            }
            long lengthBefore = fi.Length;
            var stampBefore = fi.LastWriteTimeUtc;
            act = ActivityLog.Begin("update", fi.Name, lengthBefore);
            RemoteItem item;
            try { item = await graph.UpdateFileAsync(cur.Id, cur.ETag, path, ct, ProgressLogger(fi.Name, act)).ConfigureAwait(false); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                var latest = await graph.GetItemAsync(cur.Id, ct).ConfigureAwait(false);
                if (latest.Size == lengthBefore && latest.QuickXorHash is { } lh && lh == QuickXorHash.OfFile(path, ct))
                {
                    // Our own earlier attempt had in fact finished (timeout hid the answer): the cloud already holds this exact content.
                    index.ApplyPage([DeltaChange.Upsert(latest)], "upload.last", latest.Id, index.Generation);
                    if (!PlaceholderWriter.RefreshInSync(path, latest) && !RemoteApplier.MarkInSync(path)) { ConvertToPlaceholder(path, latest.Id, onDemand: false); }
                    index.RemoveUpload(path);
                    ActivityLog.End(act, "ok", "already uploaded"); act = null;
                    PinWatcher.NotifyShell(path);
                    Log.Info($"'{fi.Name}' was already updated in the cloud by an earlier attempt: no conflict copy");
                    return;
                }
                string copyPath = Path.Combine(Path.GetDirectoryName(path)!, ConflictName(fi.Name));
                File.Copy(path, copyPath);                                                   // the user's version, as a plain new file: uploaded like any new file
                var cloud = await graph.GetItemAsync(cur.Id, ct).ConfigureAwait(false);
                index.ApplyPage([DeltaChange.Upsert(cloud)], "upload.last", cloud.Id, index.Generation);
                RemoteApplier.ForceCloudVersion(path, cloud);
                index.RemoveUpload(path);
                index.EnqueueUploads([copyPath], DateTime.UtcNow.Ticks);
                ActivityLog.End(act, "ok", "the cloud changed first: your version was saved as a conflict copy"); act = null;
                Log.Warn($"edit conflict on '{fi.Name}': your version kept as '{Path.GetFileName(copyPath)}'");
                return;
            }
            index.ApplyPage([DeltaChange.Upsert(item)], "upload.last", item.Id, index.Generation);
            var after = new FileInfo(path);
            if (after.Length != lengthBefore || after.LastWriteTimeUtc != stampBefore)
            {
                ActivityLog.End(act, "ok", "uploaded; the file changed again, uploading the newer version"); act = null;
                Defer(path, attempts, TimeSpan.FromSeconds(5), bump: false);                  // the index now holds the new eTag, so the next round is an ordinary edit
                return;
            }
            // The metadata refresh can fail (0x80070006 seen on files created locally and edited soon after): never leave the icon on "syncing" because of it.
            if (!PlaceholderWriter.RefreshInSync(path, item) && !RemoteApplier.MarkInSync(path))
            {
                if (ConvertToPlaceholder(path, item.Id, onDemand: false)) { Log.Info($"'{fi.Name}' converted again to a placeholder to mark it in sync"); }
            }
            index.RemoveUpload(path);
            ActivityLog.End(act, "ok"); act = null;
            PinWatcher.NotifyShell(path);
            Log.Info($"updated '{fi.Name}' in the cloud ({lengthBefore:N0} bytes)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // The item no longer exists in the cloud: keep the edited content as a new file instead of losing it.
            try
            {
                string copyPath = Path.Combine(Path.GetDirectoryName(path)!, ConflictName(Path.GetFileName(path)));
                File.Copy(path, copyPath);
                index.EnqueueUploads([copyPath], DateTime.UtcNow.Ticks);
                if (act is not null) { ActivityLog.End(act, "ok", "deleted in the cloud: your edit was kept as a new file"); act = null; }
            }
            catch (IOException) { /* will be retried */ }
            index.RemoveUpload(path);
        }
        catch (Exception ex)
        {
            var wait = Backoff[Math.Min(attempts, Backoff.Length - 1)];
            if (act is not null) { ActivityLog.End(act, "failed", ex.Message); act = null; } else { ActivityLog.Add("error", Path.GetFileName(path), "failed", detail: ex.Message); }
            Log.Warn($"edit upload of '{Path.GetFileName(path)}' failed ({ex.Message}); retry in {wait.TotalSeconds:F0} s");
            Defer(path, attempts, wait, bump: true);
        }
    }

    private static RemoteItem ToRemote(IndexedItem i) => new(i.Id, i.ParentId, i.Name, i.Size, new DateTimeOffset(i.ModifiedUtcTicks, TimeSpan.Zero), i.IsFolder, i.ETag, i.CTag, i.QuickXorHash);

    /// <summary>Same name, size and modified time (within 2 s): downloaded files keep the cloud's modified time, so this is the same file, not an edit.</summary>
    private static bool SameFile(IndexedItem remote, string path)
    {
        try { return remote.Size == new FileInfo(path).Length && Math.Abs(remote.ModifiedUtcTicks - File.GetLastWriteTimeUtc(path).Ticks) <= 2 * TimeSpan.TicksPerSecond; }
        catch (IOException) { return false; }
    }

    private static string ConflictName(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        return $"{stem} (NoDrive conflict {DateTime.Now:yyyy-MM-dd HHmmss}){ext}";
    }

    private void QueueChildren(string dir)
    {
        try
        {
            var kids = Directory.EnumerateFileSystemEntries(dir).Where(e => !Ignored(e) && IsRealLocal(e)).ToList();
            if (kids.Count > 0) { index.EnqueueUploads(kids, DateTime.UtcNow.Ticks); }
        }
        catch (Exception ex) { Log.Warn($"could not list {dir}: {ex.Message}"); }
    }

    /// <summary>Turns the uploaded local file or folder into an in-sync placeholder (content stays on disk).</summary>
    private static unsafe bool ConvertToPlaceholder(string path, string itemId, bool onDemand)
    {
        byte[] id = System.Text.Encoding.UTF8.GetBytes(itemId);
        var flags = CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC | (onDemand ? CF_CONVERT_FLAGS.CF_CONVERT_FLAG_ENABLE_ON_DEMAND_POPULATION : 0);
        HANDLE h = default;
        bool plainHandle = false;
        fixed (char* p = path)
        {
            // 1) exclusive oplock; 2) shared; 3) attribute-only handle with full sharing, which works while Explorer has the folder open (sharing violation 0x80070020).
            HRESULT open = PInvoke.CfOpenFileWithOplock(new PCWSTR(p), CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS | CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_EXCLUSIVE, &h);
            if (open.Failed) { open = PInvoke.CfOpenFileWithOplock(new PCWSTR(p), CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS, &h); }
            if (open.Failed)
            {
                h = PInvoke.CreateFile(new PCWSTR(p), 0x100 | 0x80, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS, HANDLE.Null);
                plainHandle = true;
                if (h.IsNull || (nint)h.Value == -1) { Log.Warn($"convert: open {path} -> 0x{open.Value:X8}"); return false; }
            }
        }
        try
        {
            fixed (byte* pid = id)
            {
                HRESULT hr = PInvoke.CfConvertToPlaceholder(h, pid, (uint)id.Length, flags, null, null);
                if (hr.Failed) { Log.Warn($"convert {path} -> 0x{hr.Value:X8}{(plainHandle ? " (attribute-only handle)" : "")}"); }
                return hr.Succeeded;
            }
        }
        finally { if (plainHandle) { PInvoke.CloseHandle(h); } else { PInvoke.CfCloseHandle(h); } }
    }
}
