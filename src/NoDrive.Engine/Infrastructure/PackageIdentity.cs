using System.Runtime.InteropServices;

namespace NoDrive.Engine.Infrastructure;

/// <summary>Whether the process runs inside an MSIX package (Microsoft Store build). Cheap and thread-safe; no I/O.</summary>
internal static partial class PackageIdentity
{
    private const int NoPackage = 15700;   // APPMODEL_ERROR_NO_PACKAGE

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetCurrentPackageFullName(ref uint length, char* name);

    private static readonly Lazy<bool> s_packaged = new(() =>
    {
        uint length = 0;
        unsafe { return GetCurrentPackageFullName(ref length, null) != NoPackage; }
    });

    /// <summary>True for the MSIX (Store) build: autostart is a manifest startup task and uninstall is done by Windows.</summary>
    public static bool IsPackaged => s_packaged.Value;
}
