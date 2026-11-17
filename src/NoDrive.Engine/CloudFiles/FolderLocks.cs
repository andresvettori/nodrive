using System.Collections.Concurrent;
using NoDrive.Core.Index;

namespace NoDrive.Engine.CloudFiles;

/// <summary>One lock per folder id so on-demand population and background prefetch never create the same placeholders at once.</summary>
internal sealed class FolderLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    public SemaphoreSlim For(string folderId) => _locks.GetOrAdd(folderId, _ => new SemaphoreSlim(1, 1));
}

/// <summary>Population state per folder id. The drive root has no item row, so its flag lives in the meta table.</summary>
internal sealed class PopulationState
{
    private const string RootKey = "root.populated";
    private readonly ItemIndex _index;
    public PopulationState(ItemIndex index, string rootId) { _index = index; RootId = rootId; }
    public string RootId { get; }

    /// <summary>Disk path of a folder id (names from the index), or null when an ancestor is unknown.</summary>
    public string? PathOfFolder(string folderId, string syncRoot)
    {
        if (folderId == RootId) { return syncRoot; }
        var chain = _index.PathTo(folderId, RootId);
        var parts = new string[chain.Count + 1];
        parts[0] = syncRoot;
        for (int i = 0; i < chain.Count; i++) { if (_index.GetName(chain[i]) is not { } n) { return null; } parts[i + 1] = n; }
        return Path.Combine(parts);
    }

    public bool IsPopulated(string folderId) => folderId == RootId ? _index.GetMeta(RootKey) == "1" : _index.TryGet(folderId)?.Populated == true;

    public void Set(string folderId, bool populated)
    {
        if (populated) { MarkChildren(folderId); }
        if (folderId == RootId) { _index.SetMeta(RootKey, populated ? "1" : "0"); }
        else { _index.SetPopulated(folderId, populated); }
    }

    /// <summary>The folder's placeholders now exist on disk: remember it (the offline reconcile only judges items known to exist), except names Windows cannot hold.</summary>
    private void MarkChildren(string folderId)
    {
        try
        {
            _index.MarkChildrenLocal(folderId);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int skipped = 0;
            foreach (var c in _index.EnumerateChildren(folderId))
            {
                if (ChildFilter.Problem(c, seen) is not { } why) { continue; }
                _index.MarkNotLocal(c.Id);
                if (++skipped <= 10) { Infrastructure.ActivityLog.Add("error", c.Name, "skipped", detail: $"cannot exist on Windows ({why})"); }
            }
            if (skipped > 0) { Infrastructure.Log.Warn($"{skipped} item(s) in folder {folderId} cannot be created on Windows and were skipped"); }
        }
        catch (Exception ex) { Infrastructure.Log.Warn($"marking local items failed: {ex.Message}"); }
    }

    public void ResetAll()
    {
        _index.ResetPopulation();
        _index.SetMeta(RootKey, "0");
    }

    public HashSet<string> PopulatedSet()
    {
        var set = _index.PopulatedFolderIds();
        if (_index.GetMeta(RootKey) == "1") { set.Add(RootId); }
        return set;
    }
}
