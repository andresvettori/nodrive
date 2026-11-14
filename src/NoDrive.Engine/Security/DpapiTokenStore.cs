using System.Runtime.InteropServices;
using System.Text;
using NoDrive.Graph.Auth;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Cryptography;

namespace NoDrive.Engine.Security;

/// <summary>
/// Refresh token encrypted with DPAPI (current user scope) in a file. Written atomically (temp + replace) because the refresh token
/// ROTATES on every use: a torn write would lose the sign-in.
/// </summary>
internal sealed unsafe class DpapiTokenStore(string path) : ITokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NoDrive.RefreshToken.v1");

    public ValueTask<string?> LoadRefreshTokenAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) { return new ValueTask<string?>((string?)null); }
        try
        {
            byte[] plain = Unprotect(File.ReadAllBytes(path));
            return new ValueTask<string?>(Encoding.UTF8.GetString(plain));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return new ValueTask<string?>((string?)null);               // unreadable (another user/profile reset): behave as signed out
        }
    }

    public ValueTask SaveRefreshTokenAsync(string refreshToken, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Protect(Encoding.UTF8.GetBytes(refreshToken)));
        File.Move(tmp, path, overwrite: true);
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken ct)
    {
        if (File.Exists(path)) { File.Delete(path); }
        return ValueTask.CompletedTask;
    }

    private static byte[] Protect(byte[] data)
    {
        fixed (byte* pData = data)
        fixed (byte* pEnt = Entropy)
        {
            var input = new CRYPT_INTEGER_BLOB { cbData = (uint)data.Length, pbData = pData };
            var entropy = new CRYPT_INTEGER_BLOB { cbData = (uint)Entropy.Length, pbData = pEnt };
            CRYPT_INTEGER_BLOB output;
            if (!PInvoke.CryptProtectData(&input, null, &entropy, null, null, 0, &output)) { throw new InvalidOperationException($"CryptProtectData failed: {Marshal.GetLastPInvokeError()}"); }
            try { return new ReadOnlySpan<byte>(output.pbData, (int)output.cbData).ToArray(); }
            finally { PInvoke.LocalFree(new HLOCAL(output.pbData)); }
        }
    }

    private static byte[] Unprotect(byte[] data)
    {
        fixed (byte* pData = data)
        fixed (byte* pEnt = Entropy)
        {
            var input = new CRYPT_INTEGER_BLOB { cbData = (uint)data.Length, pbData = pData };
            var entropy = new CRYPT_INTEGER_BLOB { cbData = (uint)Entropy.Length, pbData = pEnt };
            CRYPT_INTEGER_BLOB output;
            if (!PInvoke.CryptUnprotectData(&input, null, &entropy, null, null, 0, &output)) { throw new InvalidOperationException($"CryptUnprotectData failed: {Marshal.GetLastPInvokeError()}"); }
            try { return new ReadOnlySpan<byte>(output.pbData, (int)output.cbData).ToArray(); }
            finally { PInvoke.LocalFree(new HLOCAL(output.pbData)); }
        }
    }
}
