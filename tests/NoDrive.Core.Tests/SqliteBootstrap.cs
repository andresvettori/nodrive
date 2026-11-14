using System.Runtime.CompilerServices;

namespace NoDrive.Core.Tests;

internal static class SqliteBootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
#if WINSQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());   // the system SQLite, as in production
#else
        SQLitePCL.Batteries_V2.Init();
#endif
    }
}
