namespace NoDrive.Core;

/// <summary>
/// Sync state of a single item as tracked in the on-disk index. Transitions are owned by the
/// reconciler; see memory_bank/systemPatterns.md ("Per-item state machine").
/// </summary>
public enum ItemState : byte
{
    /// <summary>Known remotely, no placeholder created yet (lazy population).</summary>
    RemoteOnly = 0,

    /// <summary>Placeholder exists locally, content lives only in OneDrive.</summary>
    Placeholder = 1,

    /// <summary>A hydration (download) request is in flight.</summary>
    Hydrating = 2,

    /// <summary>Fully hydrated and identical to the remote version (in sync).</summary>
    InSync = 3,

    /// <summary>Local content differs from the remote version and has not been uploaded.</summary>
    LocallyModified = 4,

    /// <summary>An upload is queued or in flight.</summary>
    Uploading = 5,

    /// <summary>Local and remote changed independently; a conflict copy is required.</summary>
    Conflict = 6,

    /// <summary>A remote delete is journaled and pending execution.</summary>
    DeletePending = 7,

    /// <summary>The last operation failed; details are in the journal.</summary>
    Error = 8,
}
