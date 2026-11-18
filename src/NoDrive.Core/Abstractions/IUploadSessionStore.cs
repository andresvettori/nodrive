namespace NoDrive.Core.Abstractions;

/// <summary>An upload session that was created but not finished: where to continue it and which local file it belongs to.</summary>
public readonly record struct SavedUploadSession(string Url, long Size, long LastWriteTicks, long ExpiresTicks = 0);

/// <summary>
/// Durable memory of open upload sessions, so a retry (after a network cut, a sleep or a restart) CONTINUES the same session instead of starting a new one
/// next to an orphan that still reserves the file name. Implemented by the item index (SQLite); a fake in tests.
/// </summary>
public interface IUploadSessionStore
{
    SavedUploadSession? Find(string key);
    void Remember(string key, SavedUploadSession session);
    void Forget(string key);
}
