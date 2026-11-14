namespace NoDrive.Engine.Infrastructure;

/// <summary>Where the engine keeps its state. Everything user-specific lives under %LOCALAPPDATA%\NoDrive (never in the sync root).</summary>
internal sealed record EnginePaths(string SyncRoot, string DataDir, string IndexPath, string TokenPath, string ConfigPath, string LogDir)
{
    public static EnginePaths Create(string? syncRootOverride = null, string? dataDirOverride = null)
    {
        string data = dataDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoDrive");
        string root = syncRootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NoDrive");
        return new EnginePaths(root, data, Path.Combine(data, "index.db"), Path.Combine(data, "token.bin"), Path.Combine(data, "config.json"), Path.Combine(data, "logs"));
    }
}
