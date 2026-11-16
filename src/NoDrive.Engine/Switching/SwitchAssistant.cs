using NoDrive.Core.Index;
using NoDrive.Engine.CloudFiles;
using NoDrive.Engine.Infrastructure;

namespace NoDrive.Engine.Switching;

/// <summary>Outcome of the read-only comparison between a folder on disk (usually OneDrive's) and the cloud index.</summary>
internal sealed class ScanResult
{
    public bool Running { get; set; }
    public bool Done { get; set; }
    public string Folder { get; set; } = "";
    public long Scanned { get; set; }
    public long CloudOnly { get; set; }
    public long Same { get; set; }
    public long Differs { get; set; }
    public long LocalOnly { get; set; }
    public List<string> DiffersSample { get; set; } = [];
    public List<string> LocalOnlySample { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>
/// "Switch from OneDrive": a read-only scan that tells what would be linked, downloaded or left alone, and the take-over step that points NoDrive at
/// the old OneDrive folder. It never changes OneDrive, never deletes or overwrites a file, and refuses to run while OneDrive still owns the folder.
/// Take-over only edits NoDrive's own config and journal; the identical files are then linked by the upload service (same name, size and modified time).
/// </summary>
internal sealed class SwitchAssistant(ItemIndex index, string rootId, EnginePaths paths, EngineConfig config)
{
    private const int SampleSize = 40;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000, Offline = FileAttributes.Offline;
    private readonly object _gate = new();
    private ScanResult _scan = new();

    public ScanResult Snapshot()
    {
        lock (_gate)
        {
            return new ScanResult
            {
                Running = _scan.Running, Done = _scan.Done, Folder = _scan.Folder, Scanned = _scan.Scanned, CloudOnly = _scan.CloudOnly, Same = _scan.Same,
                Differs = _scan.Differs, LocalOnly = _scan.LocalOnly, DiffersSample = [.. _scan.DiffersSample], LocalOnlySample = [.. _scan.LocalOnlySample], Error = _scan.Error,
            };
        }
    }

    /// <summary>Starts the scan in the background (low priority, local metadata only). Returns false when one is already running or the folder is unusable.</summary>
    public bool StartScan(string folder, out string? error)
    {
        error = null;
        if (!Directory.Exists(folder)) { error = "folder-missing"; return false; }
        lock (_gate)
        {
            if (_scan.Running) { error = "already-running"; return false; }
            _scan = new ScanResult { Running = true, Folder = folder };
        }
        var ct = CancellationToken.None;                  // the scan is short-lived and read-only; it simply ends with the process
        _ = Task.Run(() =>
        {
            try { Walk(folder, rootId, ct); lock (_gate) { _scan.Done = true; } }
            catch (OperationCanceledException) { }
            catch (Exception ex) { lock (_gate) { _scan.Error = ex.Message; } Log.Warn($"switch scan failed: {ex.Message}"); }
            finally { lock (_gate) { _scan.Running = false; } }
        }, CancellationToken.None);
        return true;
    }

    private void Walk(string dir, string parentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            string name = entry.Name;
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith(".849C9593", StringComparison.Ordinal)) { continue; }
            var attrs = entry.Attributes;
            lock (_gate) { _scan.Scanned++; }
            if ((attrs & FileAttributes.Directory) != 0)
            {
                // A folder OneDrive has not populated yet: do not enumerate it (that would make OneDrive download its listing).
                if ((attrs & RecallOnOpen) != 0 || (attrs & RecallOnDataAccess) != 0) { lock (_gate) { _scan.CloudOnly++; } continue; }
                var child = index.FindChild(parentId, name);
                if (child is not { IsFolder: true } c) { CountLocalOnlyTree(entry.FullName, ct); continue; }
                Walk(entry.FullName, c.Id, ct);
                continue;
            }
            if ((attrs & (Offline | RecallOnDataAccess)) != 0) { lock (_gate) { _scan.CloudOnly++; } continue; }
            var remote = index.FindChild(parentId, name);
            var file = (FileInfo)entry;
            lock (_gate)
            {
                if (remote is not { IsFolder: false } r) { _scan.LocalOnly++; AddSample(_scan.LocalOnlySample, file.FullName); }
                else if (r.Size == file.Length && Math.Abs(r.ModifiedUtcTicks - file.LastWriteTimeUtc.Ticks) <= 2 * TimeSpan.TicksPerSecond) { _scan.Same++; }
                else { _scan.Differs++; AddSample(_scan.DiffersSample, file.FullName); }
            }
        }
    }

    private void CountLocalOnlyTree(string dir, CancellationToken ct)
    {
        long n = 0;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)) { ct.ThrowIfCancellationRequested(); n++; if (n == 1) { lock (_gate) { AddSample(_scan.LocalOnlySample, f.FullName); } } }
        lock (_gate) { _scan.LocalOnly += n; _scan.Scanned += n; }
    }

    private static void AddSample(List<string> list, string path) { if (list.Count < SampleSize) { list.Add(path); } }

    /// <summary>Reasons the take-over cannot run right now (empty means it can).</summary>
    public List<string> Blockers(string folder, long pendingUploads, long pendingOps, bool indexReady)
    {
        var b = new List<string>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) { b.Add("folder-missing"); return b; }
        string full = Path.GetFullPath(folder).TrimEnd('\\');
        string current = Path.GetFullPath(paths.SyncRoot).TrimEnd('\\');
        if (string.Equals(full, current, StringComparison.OrdinalIgnoreCase)) { b.Add("same-folder"); }
        else if (full.StartsWith(current + "\\", StringComparison.OrdinalIgnoreCase) || current.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) { b.Add("nested"); }
        if (SyncRootRegistrar.IsInsideForeignSyncRoot(full, out _)) { b.Add("onedrive-linked"); }
        if (pendingUploads + pendingOps > 0) { b.Add("pending-work"); }
        if (!indexReady) { b.Add("index-not-ready"); }
        return b;
    }

    /// <summary>
    /// Points NoDrive at <paramref name="folder"/>: stores the new root and an "adopt" marker, then the caller restarts the engine.
    /// On the next start the old registration is replaced, the identical files are linked and the rest appears as online-only files.
    /// </summary>
    public bool TakeOver(string folder, out string? error)
    {
        error = null;
        var blockers = Blockers(folder, index.CountUploads(), index.CountOps(), true);
        if (blockers.Count > 0) { error = string.Join(",", blockers); return false; }
        config.SyncRoot = Path.GetFullPath(folder).TrimEnd('\\');
        config.Save(paths.ConfigPath);
        index.SetMeta("adopt.pending", "1");
        index.SetMeta("root.populated", "0");
        ActivityLog.Add("sync", "NoDrive", "ok", detail: "switching to " + config.SyncRoot);
        Log.Info($"switch: NoDrive will use {config.SyncRoot} after the restart");
        return true;
    }

    public static void RestartEngine()
    {
        if (Environment.ProcessPath is not { } exe) { return; }
        // A short delay lets this process release its single-instance lock before the new one starts.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c ping -n 5 127.0.0.1 >nul & \"{exe}\"") { UseShellExecute = false, CreateNoWindow = true });
    }
}
