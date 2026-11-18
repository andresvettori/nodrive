namespace NoDrive.Core.Sync;

/// <summary>What to do when an upload of a NEW file meets a cloud item with the same name.</summary>
public enum ClashAction
{
    /// <summary>The cloud file IS ours (same size and quickXorHash): link it, upload nothing.</summary>
    LinkExisting,
    /// <summary>An empty cloud file with our name that we never saw: the leftover of an interrupted upload session. Remove it and retry.</summary>
    ClearEmptyLeftover,
    /// <summary>Nothing complete is visible under the name (a session still reserves it): wait and retry later, no copy.</summary>
    WaitForReservedName,
    /// <summary>A genuinely different file owns the name: keep both, ours becomes a conflict copy.</summary>
    ConflictCopy,
}

/// <summary>Pure decision rules behind the upload and delete safety paths, kept free of I/O so they are unit tested.</summary>
public static class ClashPolicy
{
    /// <summary>Attempts after which an item without hash is no longer assumed to be our own unfinished upload.</summary>
    public const int WaitAttempts = 4;

    /// <param name="cloudExists">Something is visible in the cloud under this name.</param>
    /// <param name="cloudIsFolder">That something is a folder.</param>
    /// <param name="localHash">Computes the local quickXorHash; only called when sizes are equal and the cloud has a hash.</param>
    /// <param name="leftoverRemovalFailed">The caller already tried <see cref="ClashAction.ClearEmptyLeftover"/> and it did not work.</param>
    public static ClashAction Decide(long localSize, bool cloudExists, bool cloudIsFolder, long cloudSize, string? cloudHash, int attempts, Func<string> localHash, bool leftoverRemovalFailed = false)
    {
        if (cloudExists && !cloudIsFolder)
        {
            if (cloudSize == localSize && cloudHash is not null && cloudHash == localHash()) { return ClashAction.LinkExisting; }
            if (cloudSize == 0 && localSize > 0 && attempts < WaitAttempts && !leftoverRemovalFailed) { return ClashAction.ClearEmptyLeftover; }
        }
        if (!cloudExists || (cloudHash is null && attempts < WaitAttempts)) { return ClashAction.WaitForReservedName; }
        return ClashAction.ConflictCopy;
    }
}

/// <summary>Is a local file different from the cloud copy we know?</summary>
public static class ChangeDetection
{
    /// <summary>Largest file whose content hash is computed just to avoid an upload.</summary>
    public const long MaxHashBytes = 512L * 1024 * 1024;

    /// <summary>Size differs, or the modified time differs by more than 2 seconds (the cloud and NTFS round times differently).</summary>
    public static bool IsEdited(long cloudSize, long cloudModifiedTicks, long localLength, long localModifiedTicks) =>
        cloudSize != localLength || Math.Abs(cloudModifiedTicks - localModifiedTicks) > 2 * TimeSpan.TicksPerSecond;

    /// <summary>Same size and same non-null hash.</summary>
    public static bool SameContent(long sizeA, string? hashA, long sizeB, string? hashB) => sizeA == sizeB && hashA is not null && hashA == hashB;

    /// <summary>
    /// The local file has the content of the cloud copy (a "touch", a save without changes). The local hash is computed lazily: only when sizes are equal, the cloud hash is known and the file is not huge.
    /// </summary>
    public static bool LocalEqualsCloud(long cloudSize, string? cloudHash, long localLength, Func<string> localHash) =>
        cloudHash is not null && cloudSize == localLength && localLength <= MaxHashBytes && cloudHash == localHash();
}

/// <summary>When files missing on disk are suspicious enough that nothing may be deleted in the cloud.</summary>
public static class MissingGuard
{
    public static long Limit(long localCount) => Math.Max(50, localCount / 20);
    public static bool ShouldHold(int missing, long localCount) => missing > Limit(localCount);
}
