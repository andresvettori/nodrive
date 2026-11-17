using NoDrive.Core.Index;
using NoDrive.Core.Model;
using NoDrive.Engine.Infrastructure;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;

namespace NoDrive.Engine.CloudFiles;

/// <summary>
/// Mirrors remote changes into folders that already exist on disk (E4b). Runs on the ingestion thread, never inside a callback.
/// Only touches paths whose parent folder is populated; everything else is created lazily by FETCH_PLACEHOLDERS or the prefetcher.
/// Real (non-placeholder) local files are never deleted or overwritten. Failures are logged and skipped: the index stays correct.
/// </summary>
internal sealed unsafe class RemoteApplier(ItemIndex index, PopulationState population, FolderLocks locks, string syncRoot, string rootId)
{
    private sealed record Old(IndexedItem Item, string? Path);

    /// <summary>Captures the pre-update state of the changed items; the returned action applies the changes to disk once the index is updated.</summary>
    public Action? Before(IReadOnlyList<DeltaChange> changes)
    {
        if (changes.Count == 0) { return null; }
        try
        {
            var olds = new Dictionary<string, Old>(StringComparer.Ordinal);
            foreach (var ch in changes)
            {
                var item = index.TryGet(ch.Id);
                if (item is { } it) { olds[ch.Id] = new Old(it, PathOf(it)); }
            }
            return () => Apply(changes, olds);
        }
        catch (Exception ex) { Log.Warn($"remote apply (capture) failed: {ex.Message}"); return null; }
    }

    private void Apply(IReadOnlyList<DeltaChange> changes, Dictionary<string, Old> olds)
    {
        int created = 0, removed = 0, moved = 0, updated = 0;
        var dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in changes)
        {
            try
            {
                if (index.HasPendingOp(ch.Id)) { continue; }                      // the user's own delete/rename of this item is still on its way to the cloud
                olds.TryGetValue(ch.Id, out var old);
                if (old?.Path is { } oldPath && Path.GetDirectoryName(oldPath) is { } od) { dirty.Add(od); }
                if (index.TryGet(ch.Id) is { } nw && population.IsPopulated(nw.ParentId ?? rootId) && PathOf(nw) is { } np && Path.GetDirectoryName(np) is { } nd) { dirty.Add(nd); }
                if (ch.Item is null)
                {
                    if (old?.Path is { } p && !EditedLocally(p, old.Item) && DeleteLocal(p)) { removed++; ActivityLog.Add("delete", Path.GetFileName(p), "ok", detail: "removed in the cloud"); }
                    continue;
                }
                var now = index.TryGet(ch.Id);
                if (now is not { } cur) { continue; }
                bool parentOnDisk = population.IsPopulated(cur.ParentId ?? rootId);
                string? newPath = parentOnDisk ? PathOf(cur) : null;
                bool existsOld = old?.Path is { } op && Exists(op);
                if (old is null || !existsOld)
                {
                    if (newPath is not null && !Exists(newPath)) { int c = Create(cur); created += c; if (c > 0) { ActivityLog.Add("create", cur.Name, "ok", cur.Size, detail: "new in the cloud"); } }
                    continue;
                }
                if (old.Path != newPath)
                {
                    if (newPath is null) { if (DeleteLocal(old.Path!)) { removed++; } }
                    else if (MoveLocal(old.Path!, newPath)) { moved++; ActivityLog.Add("rename", cur.Name, "ok", detail: "renamed in the cloud"); }
                    continue;
                }
                if (!cur.IsFolder && (cur.Size != old.Item.Size || cur.ModifiedUtcTicks != old.Item.ModifiedUtcTicks || cur.ETag != old.Item.ETag) && UpdateFile(newPath!, cur)) { updated++; ActivityLog.Add("update", cur.Name, "ok", cur.Size, detail: "changed in the cloud"); }
            }
            catch (Exception ex) { Log.Warn($"remote apply of {ch.Id} failed: {ex.Message}"); }
        }
        foreach (var d in dirty) { NotifyDir(d); }
        if (created + removed + moved + updated > 0) { Log.Info($"applied to disk: {created} created, {updated} updated, {moved} moved, {removed} removed"); }
    }

    /// <summary>Tells Explorer the folder listing changed so it refreshes without F5.</summary>
    internal static void NotifyDir(string dir)
    {
        fixed (char* p = dir) { PInvoke.SHChangeNotify(Windows.Win32.UI.Shell.SHCNE_ID.SHCNE_UPDATEDIR, Windows.Win32.UI.Shell.SHCNF_FLAGS.SHCNF_PATHW | Windows.Win32.UI.Shell.SHCNF_FLAGS.SHCNF_FLUSHNOWAIT, p, null); }
    }

    private string? PathOf(IndexedItem item)
    {
        var chain = index.PathTo(item.Id, rootId);
        var parts = new string[chain.Count + 1];
        parts[0] = syncRoot;
        for (int i = 0; i < chain.Count; i++) { parts[i + 1] = index.GetName(chain[i]) ?? throw new InvalidOperationException("missing parent"); }
        return Path.Combine(parts);
    }

    /// <summary>A hydrated placeholder whose size or time no longer match what the cloud had: the user edited it. Never delete or overwrite such a file because the cloud removed or changed it.</summary>
    private static bool EditedLocally(string path, IndexedItem known)
    {
        try
        {
            if (known.IsFolder || !File.Exists(path)) { return false; }
            var fi = new FileInfo(path);
            if ((fi.Attributes & (FileAttributes.Offline | (FileAttributes)0x400000)) != 0) { return false; }          // not downloaded: cannot have local edits
            bool edited = fi.Length != known.Size || Math.Abs(fi.LastWriteTimeUtc.Ticks - known.ModifiedUtcTicks) > 2 * TimeSpan.TicksPerSecond;
            if (edited) { ActivityLog.Add("error", fi.Name, "skipped", detail: "you edited this file; it was not removed because it changed or was deleted in the cloud"); Log.Warn($"kept locally edited file {path}"); }
            return edited;
        }
        catch (IOException) { return false; }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private int Create(IndexedItem item)
    {
        if (NoDrive.Core.Names.WindowsNames.Problem(item.Name) is { } why) { ActivityLog.Add("error", item.Name, "skipped", detail: $"cannot exist on Windows ({why})"); return 0; }
        string parentId = item.ParentId ?? rootId;
        string? dir = Path.GetDirectoryName(PathOf(item));
        if (dir is null || !Directory.Exists(dir)) { return 0; }
        var gate = locks.For(parentId);
        gate.Wait();
        try { int n = PlaceholderWriter.CreateChildren(dir, [item]); if (n > 0 || Exists(Path.Combine(dir, item.Name))) { index.MarkLocal(item.Id); } return n; }
        finally { gate.Release(); }
    }

    /// <summary>Deletes a placeholder (reparse point) only; a plain local file or a folder holding one is left alone.</summary>
    private static bool DeleteLocal(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) { Log.Warn($"remote delete skipped, local file is not a placeholder: {path}"); return false; }
                File.Delete(path);
                return true;
            }
            if (Directory.Exists(path))
            {
                if (HasRealFile(path)) { Log.Warn($"remote delete skipped, folder holds local files: {path}"); return false; }
                Directory.Delete(path, recursive: true);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"remote delete of {path} failed (in use?): {ex.Message}"); }
        return false;
    }

    private static bool HasRealFile(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(f => (File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0);

    private static bool MoveLocal(string from, string to)
    {
        try
        {
            if (Exists(to)) { Log.Warn($"remote move skipped, target exists: {to}"); return false; }
            if (Directory.Exists(from)) { Directory.Move(from, to); } else { File.Move(from, to); }
            MarkInSync(to);                          // a rename by a non-provider clears the in-sync flag: restore it, the rename came FROM the cloud
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"remote move {from} -> {to} failed: {ex.Message}"); return false; }
    }

    /// <summary>After a version conflict: make the file show the cloud's version (forced in sync, content dropped); the user's own version was saved as a conflict copy first.</summary>
    internal static bool ForceCloudVersion(string path, RemoteItem cloud)
    {
        MarkInSync(path);
        return UpdateFile(path, new IndexedItem(cloud.Id, cloud.ParentId, cloud.Name, cloud.Size, cloud.ModifiedUtc.UtcTicks, false, cloud.ETag, cloud.CTag, cloud.QuickXorHash, false));
    }

    internal static void MarkInSync(string path)
    {
        HANDLE h = default;
        fixed (char* p = path)
        {
            HRESULT open = PInvoke.CfOpenFileWithOplock(new PCWSTR(p), CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS, &h);
            if (open.Failed) { Log.Warn($"mark in sync: open {path} -> 0x{open.Value:X8}"); return; }
        }
        try
        {
            HRESULT hr = PInvoke.CfSetInSyncState(h, CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC, CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE, null);
            if (hr.Failed) { Log.Warn($"mark in sync: {path} -> 0x{hr.Value:X8}"); }
        }
        finally { PInvoke.CfCloseHandle(h); }
    }

    /// <summary>Refreshes size/time/identity of a placeholder and drops stale content (dehydrate) so the next open downloads the new version.</summary>
    private static bool UpdateFile(string path, IndexedItem item)
    {
        HANDLE h = default;
        fixed (char* p = path)
        {
            HRESULT open = PInvoke.CfOpenFileWithOplock(new PCWSTR(p), CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS | CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_EXCLUSIVE, &h);
            if (open.Failed) { Log.Warn($"remote update: {path} busy or unavailable 0x{open.Value:X8}"); return false; }
        }
        try
        {
            long when = new DateTime(item.ModifiedUtcTicks, DateTimeKind.Utc).ToFileTimeUtc();
            var meta = new CF_FS_METADATA { FileSize = item.Size, BasicInfo = new FILE_BASIC_INFO { CreationTime = when, LastWriteTime = when, LastAccessTime = when, ChangeTime = when, FileAttributes = (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL } };
            byte[] id = System.Text.Encoding.UTF8.GetBytes(item.Id);
            fixed (byte* pid = id)
            {
                HRESULT hr = PInvoke.CfUpdatePlaceholder(h, &meta, pid, (uint)id.Length, null, 0, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DEHYDRATE | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC, null, null);
                if (hr.Failed) { Log.Warn($"remote update of {path} -> 0x{hr.Value:X8}"); }
                return hr.Succeeded;
            }
        }
        finally { PInvoke.CfCloseHandle(h); }
    }
}
