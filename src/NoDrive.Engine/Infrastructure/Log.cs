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
        foreach (var old in Directory.EnumerateFiles(logDir, "engine-*.log").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7))) { TryDelete(old); }
    }

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
