namespace NoDrive.Core.Hashing;

/// <summary>
/// OneDrive's quickXorHash (160 bits, 11-bit rotation per byte, file length mixed in at the end), base64 encoded like the Graph "hashes.quickXorHash" value.
/// Used to tell "the cloud already has exactly this file" from a real name clash. Streams the file with a small buffer.
/// </summary>
public static class QuickXorHash
{
    private const int Bytes = 20;

    /// <summary>Hash of a whole file. Blocking read in 64 KiB pieces; call from a worker, never from a cfapi callback.</summary>
    public static string OfFile(string path, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        return Of(fs, ct);
    }

    public static string Of(Stream s, CancellationToken ct = default)
    {
        Span<byte> data = stackalloc byte[Bytes];
        byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long total = 0;
            int bitPos = 0;
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                for (int i = 0; i < n; i++)
                {
                    int idx = bitPos >> 3, off = bitPos & 7;
                    int v = buf[i] << off;
                    data[idx] ^= (byte)v;
                    data[(idx + 1) % Bytes] ^= (byte)(v >> 8);
                    bitPos += 11; if (bitPos >= Bytes * 8) { bitPos -= Bytes * 8; }
                }
                total += n;
            }
            for (int j = 0; j < 8; j++) { data[Bytes - 8 + j] ^= (byte)(total >> (8 * j)); }
            return Convert.ToBase64String(data);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
    }
}
