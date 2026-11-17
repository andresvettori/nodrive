using NoDrive.Core.Model;
using NoDrive.Core.Names;

namespace NoDrive.Engine.CloudFiles;

/// <summary>
/// Drops cloud items that cannot exist as Windows files (invalid characters, trailing dot or space, reserved names, over 255 characters) and the second of two names that differ
/// only by case. One such name inside a batch would make the whole folder fail to open, so they are skipped here and listed in the activity page by <see cref="PopulationState"/>.
/// </summary>
internal static class ChildFilter
{
    public static IEnumerable<IndexedItem> Valid(IEnumerable<IndexedItem> children)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in children)
        {
            if (Problem(c, seen) is null) { yield return c; }
        }
    }

    public static string? Problem(IndexedItem c, HashSet<string> seen) => WindowsNames.Problem(c.Name) ?? (seen.Add(c.Name) ? null : "case-collision");
}
