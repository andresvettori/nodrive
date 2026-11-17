using System.Buffers;
using NoDrive.Engine.Infrastructure;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using NoDrive.Core.Hydration;
using NoDrive.Graph;

namespace NoDrive.Engine.CloudFiles;

/// <summary>The download failed in a way the caller should report to Windows as "provider unavailable" (never leave the request pending).</summary>
internal sealed class HydrationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Receives bytes in file order. Called synchronously; must copy what it keeps.</summary>
internal delegate void ChunkSink(ReadOnlySpan<byte> chunk, long fileOffset);

/// <summary>
/// Streams a byte range of a OneDrive file through Graph's pre-authenticated download URL into a sink, with the P0 step-7 rules:
/// a no-progress watchdog (15 s, far inside the 60 s platform deadline), fail-fast on long Retry-After, a short memory of failed items
/// (Windows re-issues failed requests), one pooled HttpClient, fixed 1 MiB pooled buffers, tolerance for servers that ignore Range (200).
/// </summary>
internal sealed class Hydrator(GraphDrive graph, HydrationPolicy policy, FailureMemory failures)
{
    private const int ChunkSize = 1024 * 1024;
    private static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(45);          // Graph URLs live about an hour

    private readonly HttpClient _http = CreateClient();

    /// <summary>Optional download rate limit shared by all downloads.</summary>
    public NoDrive.Core.Net.BandwidthLimiter? Limiter { get; init; }
    private readonly ConcurrentDictionary<string, (Uri Url, DateTime FetchedUtc)> _urls = new();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
        })
        { Timeout = Timeout.InfiniteTimeSpan };                                           // the watchdog is the only timeout
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"ISV|NoDrive|NoDrive/{BuildInfo.Version}");
        return c;
    }

    /// <param name="firstFlush">Bytes to deliver as soon as they arrive (the part a caller is blocked on); the rest streams in full chunks.</param>
    public async Task StreamAsync(string itemId, long offset, long length, long firstFlush, ChunkSink sink, Action<long> progress, CancellationToken ct, long expectedSize = -1)
    {
        if (failures.IsRecentlyFailed(itemId, out var age))
        {
            throw new HydrationException($"item failed {age.TotalSeconds:F0} s ago; failing fast (retry window {policy.FailureMemory.TotalSeconds:F0} s)");
        }
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(policy.NoProgressTimeout);
        try
        {
            await StreamCoreAsync(itemId, offset, length, firstFlush, sink, progress, expectedSize, watchdog, ct).ConfigureAwait(false);
            failures.Clear(itemId);
        }
        catch (OperationCanceledException) when (watchdog.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            failures.Record(itemId);
            throw new HydrationException($"no progress for {policy.NoProgressTimeout.TotalSeconds:F0} s; failing the request instead of leaving the caller waiting");
        }
        catch (HttpRequestException ex)
        {
            failures.Record(itemId);                                                      // throttling, not found, auth: an immediate retry is pointless
            throw new HydrationException(ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new HydrationException("transfer interrupted: " + ex.Message, ex);      // transient: not remembered, Windows retries the missing range
        }
    }

    private async Task StreamCoreAsync(string itemId, long offset, long length, long firstFlush, ChunkSink sink, Action<long> progress, long expectedSize, CancellationTokenSource watchdog, CancellationToken ct)
    {
        var token = watchdog.Token;
        long end = offset + length;                                                       // exclusive
        HttpResponseMessage resp;
        int attempt = 0, refetches = 0;
        while (true)
        {
            Uri url = await GetUrlAsync(itemId, token).ConfigureAwait(false);
            using var msg = new HttpRequestMessage(HttpMethod.Get, url);
            msg.Headers.Range = new RangeHeaderValue(offset, end - 1);
            resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

            if ((int)resp.StatusCode is 429 or 503)
            {
                var decision = policy.DecideThrottle(resp.Headers.RetryAfter?.Delta, attempt);
                resp.Dispose();
                if (!decision.Retry) { throw new HttpRequestException($"throttled: Retry-After {decision.Wait.TotalSeconds:F0} s exceeds the budget of {policy.MaxRetryAfterWait.TotalSeconds:F0} s"); }
                attempt++;
                await Task.Delay(decision.Wait, token).ConfigureAwait(false);
                watchdog.CancelAfter(policy.NoProgressTimeout);
                continue;
            }
            if ((int)resp.StatusCode is 401 or 403 or 404 && refetches++ == 0)
            {
                resp.Dispose();                                                           // the pre-authenticated URL expired: ask Graph for a new one once
                _urls.TryRemove(itemId, out _);
                continue;
            }
            break;
        }

        using var response = resp;
        response.EnsureSuccessStatusCode();
        long? actual = response.Content.Headers.ContentRange?.Length ?? (response.StatusCode == System.Net.HttpStatusCode.OK ? response.Content.Headers.ContentLength : null);
        if (expectedSize >= 0 && actual is { } a && a != expectedSize) { Log.Warn($"SIZE MISMATCH for {itemId}: the placeholder says {expectedSize} bytes but the download is {a} bytes ({(int)response.StatusCode})"); }
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            long skip = response.StatusCode == System.Net.HttpStatusCode.OK && offset > 0 ? offset : 0;   // server ignored Range: discard the prefix
            while (skip > 0)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(ChunkSize, skip)), token).ConfigureAwait(false);
                if (n == 0) { throw new IOException("stream ended while skipping to the requested offset"); }
                skip -= n;
                watchdog.CancelAfter(policy.NoProgressTimeout);
            }

            long position = offset;
            while (position < end)
            {
                int want = (int)Math.Min(ChunkSize, end - position);
                if (position == offset && firstFlush > 0)
                {
                    // Unblock the caller as early as possible: the first transfer covers just what it asked for (4 KiB aligned unless it ends the range).
                    long first = (firstFlush + 4095) / 4096 * 4096;
                    want = (int)Math.Min(want, Math.Max(4096, first));
                }
                long rate = Limiter?.BytesPerSecond ?? 0;
                if (rate > 0) { want = (int)Math.Min(want, Math.Max(4096, rate / 4 / 4096 * 4096)); }          // small steps so a low limit never trips the no-progress watchdog
                int filled = 0;
                while (filled < want)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(filled, want - filled), token).ConfigureAwait(false);
                    if (n == 0) { break; }
                    filled += n;
                    watchdog.CancelAfter(policy.NoProgressTimeout);
                }
                if (filled == 0) { throw new IOException($"stream ended at {position}, expected {end}"); }
                if (Limiter is not null) { await Limiter.WaitAsync(filled, token).ConfigureAwait(false); watchdog.CancelAfter(policy.NoProgressTimeout); }
                sink(buffer.AsSpan(0, filled), position);
                position += filled;
                progress(position - offset);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task<Uri> GetUrlAsync(string itemId, CancellationToken ct)
    {
        if (_urls.TryGetValue(itemId, out var cached) && DateTime.UtcNow - cached.FetchedUtc < UrlLifetime) { return cached.Url; }
        Uri url = await graph.GetDownloadUrlAsync(itemId, ct).ConfigureAwait(false);
        _urls[itemId] = (url, DateTime.UtcNow);
        if (_urls.Count > 2048) { foreach (var k in _urls.Where(kv => DateTime.UtcNow - kv.Value.FetchedUtc >= UrlLifetime).Select(kv => kv.Key).ToList()) { _urls.TryRemove(k, out _); } }
        return url;
    }
}
