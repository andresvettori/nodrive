using System.Collections.Concurrent;
using System.Net;
using NoDrive.Core.Index;
using NoDrive.Core.Model;
using NoDrive.Engine.Infrastructure;
using NoDrive.Graph;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

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
        _watcher = new FileSystemWatcher(syncRoot) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, InternalBufferSize = 64 * 1024 };
        _watcher.Created += (_, e) => Note(e.FullPath);
        _watcher.Renamed += (_, e) => Note(e.FullPath);
        _watcher.Error += (_, e) => { Log.Warn($"upload watcher overflow: {e.GetException().Message}; rescanning populated folders"); _rescan = true; };
        _watcher.EnableRaisingEvents = true;
        _ = Task.Run(() => LoopAsync(ct), CancellationToken.None);
    }

    public void Dispose() => _watcher?.Dispose();

    private void Note(string path)
    {
        if (_seen.Count < MaxWatched && !Ignored(path)) { _seen.TryAdd(path, 0); }
        else if (_seen.Count >= MaxWatched) { _rescan = true; }
    }

    private static bool Ignored(string path)
    {
        string n = Path.GetFileName(path);
        return n.StartsWith("~$", StringComparison.Ordinal) || n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || n.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || n.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);
    }

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
                    await Task.Delay(TimeSpan.FromSeconds(_active.Count == 0 ? 3 : 1), ct).ConfigureAwait(false);
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
        foreach (var k in _seen.Keys) { if (_seen.TryRemove(k, out _) && IsRealLocal(k)) { batch.Add(k); } }
        if (batch.Count > 0) { index.EnqueueUploads(batch, DateTime.UtcNow.Add(Quiet).Ticks); }
    }

    /// <summary>Queues real (non-placeholder) entries found in folders that exist on disk. Local I/O only, yields regularly.</summary>
    private async Task ScanPopulatedAsync(CancellationToken ct)
    {
        int n = 0, queued = 0;
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
        try
        {
            bool isDir = Directory.Exists(path);
            if ((!isDir && !File.Exists(path)) || !IsRealLocal(path)) { index.RemoveUpload(path); return; }     // gone, or already a placeholder
            string? parentId = ResolveParent(path);
            if (parentId is null) { Defer(path, attempts, TimeSpan.FromSeconds(15), bump: false); return; }       // the parent folder is still queued: wait for it

            string name = Path.GetFileName(path);
            RemoteItem item;
            string finalPath = path;
            DateTime stamp = isDir ? default : File.GetLastWriteTimeUtc(path);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (isDir)
            {
                if (index.FindChild(parentId, name) is not null) { Defer(path, attempts, TimeSpan.FromMinutes(1), bump: true); return; }   // a remote folder of that name exists: wait for its delta, never merge blindly
                item = await graph.CreateFolderAsync(parentId, name, ct).ConfigureAwait(false);
            }
            else
            {
                try { using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
                catch (IOException) { Defer(path, attempts, TimeSpan.FromSeconds(10), bump: false); return; }          // still being written
                try { item = await graph.UploadNewFileAsync(parentId, name, path, ct, ProgressLogger(name)).ConfigureAwait(false); }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                {
                    string copy = ConflictName(name);
                    item = await graph.UploadNewFileAsync(parentId, copy, path, ct, ProgressLogger(copy)).ConfigureAwait(false);
                    finalPath = Path.Combine(Path.GetDirectoryName(path)!, copy);
                    File.Move(path, finalPath);
                    Log.Warn($"name clash for '{name}': uploaded as '{copy}'");
                }
                if (File.Exists(finalPath) && File.GetLastWriteTimeUtc(finalPath) != stamp) { Log.Warn($"{name} changed while uploading; the uploaded copy is the earlier version and the file stays a plain local file"); index.RemoveUpload(path); return; }
            }

            index.ApplyPage([DeltaChange.Upsert(item)], "upload.last", item.Id, index.Generation);
            bool converted = ConvertToPlaceholder(finalPath, item.Id);
            if (isDir) { population.Set(item.Id, true); QueueChildren(finalPath); }
            index.RemoveUpload(path);
            PinWatcher.NotifyShell(finalPath);
            if (Path.GetDirectoryName(finalPath) is { } parentDir) { RemoteApplier.NotifyDir(parentDir); }
            Log.Info($"uploaded {(isDir ? "folder" : "file")} '{Path.GetFileName(finalPath)}' in {sw.ElapsedMilliseconds} ms{(converted ? "" : " (placeholder conversion failed, will show as local until the next sync)")}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.InsufficientStorage or HttpStatusCode.NotFound)
        {
            Log.Warn($"upload of '{Path.GetFileName(path)}' refused ({ex.Message}); retrying in 6 h");
            Defer(path, attempts, TimeSpan.FromHours(6), bump: true);
        }
        catch (Exception ex)
        {
            var wait = Backoff[Math.Min(attempts, Backoff.Length - 1)];
            Log.Warn($"upload of '{Path.GetFileName(path)}' failed ({ex.Message}); retry in {wait.TotalSeconds:F0} s");
            Defer(path, attempts, wait, bump: true);
        }
    }

    /// <summary>Logs large uploads about every 10%; small files stay silent.</summary>
    private static Action<long, long> ProgressLogger(string name)
    {
        int last = 0;
        return (sent, total) =>
        {
            if (total < 20 * 1024 * 1024) { return; }
            int pct = (int)(sent * 100 / total) / 10 * 10;
            if (pct > last && pct < 100) { last = pct; Log.Info($"uploading '{name}': {pct}% ({sent / 1048576} of {total / 1048576} MiB)"); }
        };
    }

    private void Defer(string path, int attempts, TimeSpan wait, bool bump) => index.RescheduleUpload(path, bump ? attempts + 1 : attempts, DateTime.UtcNow.Add(wait).Ticks);

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
    private static unsafe bool ConvertToPlaceholder(string path, string itemId)
    {
        HANDLE h = default;
        fixed (char* p = path)
        {
            HRESULT open = PInvoke.CfOpenFileWithOplock(new PCWSTR(p), CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS | CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_EXCLUSIVE, &h);
            if (open.Failed) { Log.Warn($"convert: open {path} -> 0x{open.Value:X8}"); return false; }
        }
        try
        {
            byte[] id = System.Text.Encoding.UTF8.GetBytes(itemId);
            fixed (byte* pid = id)
            {
                HRESULT hr = PInvoke.CfConvertToPlaceholder(h, pid, (uint)id.Length, CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC, null, null);
                if (hr.Failed) { Log.Warn($"convert {path} -> 0x{hr.Value:X8}"); }
                return hr.Succeeded;
            }
        }
        finally { PInvoke.CfCloseHandle(h); }
    }
}
