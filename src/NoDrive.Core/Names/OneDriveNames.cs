namespace NoDrive.Core.Names;

/// <summary>Local names NoDrive never uploads: names OneDrive rejects or reserves, and temporary files that applications create and delete in seconds.</summary>
public static class OneDriveNames
{
    /// <summary>
    /// True for: Office lock files (<c>~$</c>), <c>.tmp</c>, <c>.crdownload</c>, <c>.part</c>, <c>desktop.ini</c>, <c>Thumbs.db</c>, <c>.lock</c>, anything containing <c>_vti_</c>,
    /// a name that starts with a space, and a folder or file called <c>forms</c> directly in the root of the drive (a SharePoint-era reserved name). Case-insensitive.
    /// </summary>
    public static bool IsNeverUploaded(string name, bool atRoot)
    {
        if (string.IsNullOrEmpty(name)) { return true; }
        return name.StartsWith("~$", StringComparison.Ordinal)
            || name[0] == ' '
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".lock", StringComparison.OrdinalIgnoreCase)
            || name.Contains("_vti_", StringComparison.OrdinalIgnoreCase)
            || (atRoot && name.Equals("forms", StringComparison.OrdinalIgnoreCase));
    }
}
