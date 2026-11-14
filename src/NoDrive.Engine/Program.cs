namespace NoDrive.Engine;

/// <summary>
/// Entry point of the always-on engine. Phase P0 (spike) fills this in step by step:
/// message-only window + tray icon, sync-root registration, Cloud Files connection.
/// Nothing here may block; see memory_bank/bestPractices.md.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        // TODO(P0): single-instance mutex, tray message loop, CfConnectSyncRoot.
        return 0;
    }
}
