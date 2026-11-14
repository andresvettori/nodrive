namespace NoDrive.Core.Model;

/// <summary>A drive item as reported by the remote (Microsoft Graph driveItem subset). Immutable.</summary>
public sealed record RemoteItem(
    string Id,
    string? ParentId,
    string Name,
    long Size,
    DateTimeOffset ModifiedUtc,
    bool IsFolder,
    string? ETag,
    string? CTag,
    string? QuickXorHash);

/// <summary>One entry of a delta page: either an upsert of an item or the deletion of an id.</summary>
public readonly record struct DeltaChange(string Id, RemoteItem? Item)
{
    public bool IsDeletion => Item is null;
    public static DeltaChange Upsert(RemoteItem item) => new(item.Id, item);
    public static DeltaChange Delete(string id) => new(id, null);
}

/// <summary>
/// One page of /delta. <see cref="NextLink"/> is set while more pages follow; the last page carries <see cref="DeltaLink"/>,
/// the token for the next incremental round. Exactly one of the two is non-null.
/// </summary>
public sealed record DeltaPage(IReadOnlyList<DeltaChange> Changes, string? NextLink, string? DeltaLink);

/// <summary>An item as stored in the local index.</summary>
public readonly record struct IndexedItem(string Id, string? ParentId, string Name, long Size, long ModifiedUtcTicks, bool IsFolder, string? ETag, string? CTag, string? QuickXorHash, bool Populated)
{
    public DateTimeOffset ModifiedUtc => new(ModifiedUtcTicks, TimeSpan.Zero);
}
