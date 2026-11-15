using System.Collections.Concurrent;
using System.Net;
using NoDrive.Core.Index;
using NoDrive.Core.Model;
using NoDrive.Engine.Infrastructure;
using NoDrive.Graph;

namespace NoDrive.Engine.CloudFiles;

/// <summary>
/// Replays deletes and renames made in Explorer to the cloud (E6). The cfapi callbacks only <see cref="Post"/> and acknowledge; this worker
/// journals each operation in SQLite before touching the network, then runs them one at a time in arrival order with backoff.
/// Safety: deletes go to the OneDrive recycle bin and carry If-Match (a file changed remotely is kept), renames never replace
/// (conflictBehavior=fail, then a conflict name), and a burst of deletes beyond the threshold is held until the user allows it in the tray.
/// </summary>
internal sealed class LocalOps(ItemIndex index, GraphDrive graph, string syncRoot, string rootId)
{
    private static readonly ConcurrentQueue<(string Kind, string Id, string? Target)> s_inbox = new();
    private static readonly SemaphoreSlim s_signal = new(0);
    private const int InboxLimit = 200_000;
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)];

    public const int MassDeleteThreshold = 200;
    private static readonly TimeSpan MassWindow = TimeSpan.FromMinutes(10);
    private readonly Queue<DateTime> _recentDeletes = new();
    private DateTime _allowUntil;
    private volatile int _held;

    /// <summary>Deletions waiting for the user's confirmation because too many arrived in a short time.</summary>
    public int HeldDeletes => _held;

    /// <summary>Lets held and new deletions through for the next ten minutes (tray action).</summary>
    public void AllowDeletes() { _allowUntil = DateTime.UtcNow.AddMinutes(10); s_signal.Release(); }

    /// <summary>Called from a cfapi callback: O(1), no I/O, never blocks.</summary>
    public static void Post(string kind, string itemId, string? target)
    {
        if (itemId.Length == 0 || s_inbox.Count >= InboxLimit) { return; }
        s_inbox.Enqueue((kind, itemId, target));
        s_signal.Release();
    }

    public void Start(CancellationToken ct) => _ = Task.Run(() => LoopAsync(ct), CancellationToken.None);

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (s_inbox.TryDequeue(out var op)) { index.EnqueueOp(op.Kind, op.Id, op.Target, DateTime.UtcNow.Ticks); }
                if (UserSettings.Paused) { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); continue; }
                var due = index.DueOps(DateTime.UtcNow.Ticks, 20);
                int ran = 0;
                foreach (var op in due)
                {
                    if (await RunAsync(op, ct).ConfigureAwait(false)) { ran++; }
                }
                if (ran > 0) { continue; }
                await s_signal.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { Log.Warn($"local ops loop: {ex.Message}"); await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
        }
    }

    /// <summary>Runs one journalled operation; true when it was executed or dropped (progress), false when deferred or held.</summary>
    private async Task<bool> RunAsync(RemoteOp op, CancellationToken ct)
    {
        try
        {
            var item = index.TryGet(op.ItemId);
            if (item is not { } it) { index.RemoveOp(op.Seq); return true; }                 // unknown or already gone remotely
            string? rel = op.Kind == "rename" ? RelativeTarget(op.Target) : null;
            bool asDelete = op.Kind == "delete" || (op.Kind == "rename" && rel is null);       // moved out of the sync root (Recycle Bin) = delete

            if (asDelete)
            {
                if (!MayDelete(op)) { return false; }
                try { await graph.DeleteItemAsync(op.ItemId, it.ETag, ct).ConfigureAwait(false); }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    Log.Warn($"'{it.Name}' changed in the cloud since; the local delete is not sent and the file will come back");
                    index.RemoveOp(op.Seq);
                    return true;
                }
                index.ApplyPage([DeltaChange.Delete(op.ItemId)], "ops.last", op.ItemId, index.Generation);
                index.RemoveOp(op.Seq);
                Log.Info($"deleted '{it.Name}' in the cloud (recycle bin)");
                return true;
            }

            string[] parts = rel!.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string newName = parts[^1];
            string? newParent = ResolveParent(parts);
            if (newParent is null) { Defer(op, TimeSpan.FromSeconds(15), bump: true); return false; }      // target folder not uploaded yet
            if (newParent == (it.ParentId ?? rootId) && string.Equals(newName, it.Name, StringComparison.Ordinal)) { index.RemoveOp(op.Seq); return true; }
            string? parentArg = newParent == (it.ParentId ?? rootId) ? null : newParent;
            string localPath = Path.Combine(syncRoot, rel);
            RemoteItem moved;
            try { moved = await MoveWithRetryAsync(op.ItemId, newName, parentArg, it.ETag, ct).ConfigureAwait(false); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                string alt = ConflictName(newName);
                moved = await graph.MoveItemAsync(op.ItemId, alt, parentArg, null, ct).ConfigureAwait(false);
                string altPath = Path.Combine(Path.GetDirectoryName(localPath)!, alt);
                if (Directory.Exists(localPath)) { Directory.Move(localPath, altPath); } else if (File.Exists(localPath)) { File.Move(localPath, altPath); }
                localPath = altPath;
                Log.Warn($"name clash for '{newName}': renamed to '{alt}'");
            }
            index.ApplyPage([DeltaChange.Upsert(moved)], "ops.last", op.ItemId, index.Generation);
            RemoteApplier.MarkInSync(localPath);
            PinWatcher.NotifyShell(localPath);
            index.RemoveOp(op.Seq);
            Log.Info($"renamed/moved '{it.Name}' to '{moved.Name}' in the cloud");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { index.RemoveOp(op.Seq); return true; }     // gone remotely: the delta removes it locally
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
        {
            Log.Warn($"{op.Kind} of {op.ItemId} refused ({ex.Message}); retrying in 6 h");
            Defer(op, TimeSpan.FromHours(6), bump: true);
            return false;
        }
        catch (Exception ex)
        {
            var wait = Backoff[Math.Min(op.Attempts, Backoff.Length - 1)];
            Log.Warn($"{op.Kind} of {op.ItemId} failed ({ex.Message}); retry in {wait.TotalSeconds:F0} s");
            Defer(op, wait, bump: true);
            return false;
        }
    }

    private async Task<RemoteItem> MoveWithRetryAsync(string id, string name, string? parent, string? etag, CancellationToken ct)
    {
        try { return await graph.MoveItemAsync(id, name, parent, etag, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed) { return await graph.MoveItemAsync(id, name, parent, null, ct).ConfigureAwait(false); }   // a rename never destroys content
    }

    private bool MayDelete(RemoteOp op)
    {
        var now = DateTime.UtcNow;
        while (_recentDeletes.Count > 0 && now - _recentDeletes.Peek() > MassWindow) { _recentDeletes.Dequeue(); }
        if (_recentDeletes.Count >= MassDeleteThreshold && now > _allowUntil)
        {
            _held = index.DueOps(now.Ticks, 100_000).Count(o => o.Kind is "delete" or "rename");
            if (_held > 0) { Log.Warn($"mass delete guard: {_recentDeletes.Count} deletions in {MassWindow.TotalMinutes:F0} min; further deletions are held until you allow them in the tray menu"); }
            return false;
        }
        _held = 0;
        _recentDeletes.Enqueue(now);
        return true;
    }

    private void Defer(RemoteOp op, TimeSpan wait, bool bump) => index.RescheduleOp(op.Seq, bump ? op.Attempts + 1 : op.Attempts, DateTime.UtcNow.Add(wait).Ticks);

    /// <summary>Target path of a rename relative to the sync root, or null when the target lies outside it.</summary>
    private string? RelativeTarget(string? target)
    {
        if (string.IsNullOrEmpty(target)) { return null; }
        string root = syncRoot.Length > 2 && syncRoot[1] == ':' ? syncRoot[2..] : syncRoot;        // callbacks give volume-relative paths
        string t = target;
        if (t.Length > 2 && t[1] == ':') { t = t[2..]; }
        if (!t.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) { return null; }
        return t[(root.Length + 1)..];
    }

    private string? ResolveParent(string[] parts)
    {
        string current = rootId;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (index.FindChild(current, parts[i]) is not { IsFolder: true } c) { return null; }
            current = c.Id;
        }
        return current;
    }

    private static string ConflictName(string name) => $"{Path.GetFileNameWithoutExtension(name)} (NoDrive conflict {DateTime.Now:yyyy-MM-dd HHmmss}){Path.GetExtension(name)}";
}
