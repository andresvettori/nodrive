using System.Text.RegularExpressions;

namespace NoDrive.Core.Support;

/// <summary>
/// Removes file and folder names from log text for a support report when the user chose to leave them out. Best effort, but biased to over-masking: anything under the sync folder
/// (up to the end of the path as the logs print it), every quoted name, and the few unquoted forms the engine logs ("free up space: NAME done").
/// </summary>
public static partial class LogMasker
{
    public static string Mask(string text, string syncRoot)
    {
        if (!string.IsNullOrEmpty(syncRoot)) { text = text.Replace(syncRoot.TrimEnd('\\', '/'), "<sync folder>", StringComparison.OrdinalIgnoreCase); }
        text = UnderSyncFolder().Replace(text, "<sync folder>\\<path>");
        text = QuotedName().Replace(text, "'...'");
        text = UnquotedName().Replace(text, "$1<name>$3");
        return AbsolutePath().Replace(text, "<path>");
    }

    // <sync folder>\anything up to " ->", ")" followed by " ->", a quote or the end of the line (folder names may contain parentheses and spaces).
    [GeneratedRegex(@"<sync folder>[\\/][^\r\n'""]*?(?=\)?\s+->|['""]|\r|\n|$)", RegexOptions.Multiline)]
    private static partial Regex UnderSyncFolder();

    [GeneratedRegex("'[^'\r\n]{1,300}'")]
    private static partial Regex QuotedName();

    // "free up space: NAME done in 3 ms", "keep on device: NAME -> 0x...", "keep on device: NAME done in ..."
    [GeneratedRegex(@"((?:free up space|keep on device|pinned|dehydrate)\s*:\s+)(.+?)(\s+(?:done|failed|->)\b|\s*$)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex UnquotedName();

    [GeneratedRegex(@"[A-Za-z]:\\[^\s'""<>|]+")]
    private static partial Regex AbsolutePath();
}
