using System.Globalization;

namespace NoDrive.Engine.Infrastructure;

/// <summary>Small thread-safe logger: console plus a daily file under the log directory (7 days kept). No dependencies, AOT safe.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? s_dir;
    private static string? s_currentFile;

    public static void Init(string logDir)
    {
        s_dir = logDir;
        Directory.CreateDirectory(logDir);
        // Keep 14 days and at most about 60 MB of logs and crash notes, oldest first.
        foreach (var old in Directory.EnumerateFiles(logDir).Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14))) { TryDelete(old); }
        var files = new DirectoryInfo(logDir).EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        long total = 0;
        foreach (var f in files) { total += f.Length; if (total > 60L * 1024 * 1024) { TryDelete(f.FullName); } }
    }

    /// <summary>First line of every run: version, session id, system, language. Lets a report be matched to its log in seconds.</summary>
    public static void Header(string header) => Write("INFO ", "==== " + header);

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}");
        lock (Gate)
        {
            try { Console.WriteLine(line); } catch (IOException) { /* no console attached */ }
            if (s_dir is null) { return; }
            try
            {
                s_currentFile ??= Path.Combine(s_dir, $"engine-{DateTime.Now:yyyyMMdd}.log");
                if (!s_currentFile.Contains(DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal)) { s_currentFile = Path.Combine(s_dir, $"engine-{DateTime.Now:yyyyMMdd}.log"); }
                File.AppendAllText(s_currentFile, line + Environment.NewLine);
            }
            catch (IOException) { /* logging must never take the engine down */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } }
}
