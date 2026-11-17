using System.Text;

namespace NoDrive.Graph.Auth;

/// <summary>
/// Stores the refresh token in one file with owner-only permissions (0600 on macOS/Linux), written atomically. Intended for tests and for non-Windows development builds;
/// the shipped macOS engine is meant to use the Keychain instead (see memory_bank/macos-plan.md). Windows uses DPAPI (Engine/Security/DpapiTokenStore).
/// </summary>
public sealed class FileTokenStore(string path) : ITokenStore
{
    public async ValueTask<string?> LoadRefreshTokenAsync(CancellationToken ct)
    {
        try { return File.Exists(path) ? (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Trim() is { Length: > 0 } t ? t : null : null; }
        catch (IOException) { return null; }
    }

    public async ValueTask SaveRefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, refreshToken, new UTF8Encoding(false), ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        File.Move(temp, path, overwrite: true);
    }

    public ValueTask ClearAsync(CancellationToken ct)
    {
        try { if (File.Exists(path)) { File.Delete(path); } } catch (IOException) { /* nothing more to do */ }
        return ValueTask.CompletedTask;
    }
}
