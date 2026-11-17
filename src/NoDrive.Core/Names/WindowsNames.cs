namespace NoDrive.Core.Names;

/// <summary>
/// Whether a OneDrive item name can exist as a Windows file name. OneDrive allows some names Windows cannot create (trailing dot or space, reserved device names,
/// characters such as ? or :). One such name inside a batch makes the whole on-demand population of a folder fail, so these are filtered out and reported instead.
/// </summary>
public static class WindowsNames
{
    private static readonly System.Buffers.SearchValues<char> Invalid = System.Buffers.SearchValues.Create("<>:\"/\\|?*");
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Returns null when the name is usable, otherwise a short reason (invalid-character, trailing-dot-or-space, reserved-name, too-long, empty).</summary>
    public static string? Problem(string name)
    {
        if (string.IsNullOrEmpty(name)) { return "empty"; }
        if (name.Length > 255) { return "too-long"; }
        if (name.AsSpan().ContainsAny(Invalid) || name.Any(char.IsControl)) { return "invalid-character"; }
        if (name[^1] is '.' or ' ') { return "trailing-dot-or-space"; }
        string stem = name.Contains('.', StringComparison.Ordinal) ? name[..name.IndexOf('.', StringComparison.Ordinal)] : name;
        if (Reserved.Contains(stem.TrimEnd(' '))) { return "reserved-name"; }
        return null;
    }

    public static bool IsValid(string name) => Problem(name) is null;
}
