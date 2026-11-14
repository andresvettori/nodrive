using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using NoDrive.Spikes.Scale;
using NoDrive.Spikes.Shared;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace NoDrive.Spikes.CfApiMinimal;

/// <summary>
/// The cfapi connection. Callbacks do nothing but copy the request into a bounded channel and return;
/// a worker streams Range downloads into CfExecute(TRANSFER_DATA) in 1 MiB chunks and reports progress.
/// Delete/rename notifications are acknowledged immediately from inside the callback (microseconds, no I/O).
/// </summary>
internal static class CloudProvider
{
    private sealed record HydrationRequest(long ConnectionKey, long TransferKey, long RequestKey, string ItemId, string Path, long FileSize, long Offset, long Length, long OptOffset, long OptLength, uint Flags, byte Priority, string? Process);

    private sealed record PopulateRequest(long ConnectionKey, long TransferKey, long RequestKey, string? FolderId, string Path, long EnqueuedTicks);

    private static readonly Channel<PopulateRequest> s_populateQueue = Channel.CreateBounded<PopulateRequest>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait });
    private static ItemStore? s_index;
    private static readonly object s_indexLock = new();
    private const string IndexRootId = "F61C08DC8B03ABFF!101";

    /// <summary>Gives the provider the SQLite index that FETCH_PLACEHOLDERS is answered from (population policy PARTIAL).</summary>
    public static void UseIndex(ItemStore index, int populateWorkers = 2)
    {
        s_index = index;
        for (int i = 0; i < populateWorkers; i++) { _ = Task.Run(PopulateWorkerAsync); }
    }

    private static readonly Channel<HydrationRequest> s_queue = Channel.CreateBounded<HydrationRequest>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait });
    private static readonly ConcurrentDictionary<long, CancellationTokenSource> s_inFlight = new();
    private static readonly ConcurrentDictionary<string, (string Url, DateTime FetchedUtc)> s_downloadUrls = new();
    private static readonly Stopwatch s_clock = Stopwatch.StartNew();
    private static readonly ConcurrentBag<double> s_callbackMicros = new();

    // Hydration source and fault injection for the P0 responsiveness tests (NODRIVE_DOWNLOAD_BASE=http://127.0.0.1:5799, NODRIVE_FAULT=stall|429|reset|ignorerange|slow).
    private static readonly string? s_downloadBase = Environment.GetEnvironmentVariable("NODRIVE_DOWNLOAD_BASE");
    private static readonly string s_fault = Environment.GetEnvironmentVariable("NODRIVE_FAULT") ?? "none";
    private static readonly TimeSpan NoProgressTimeout = TimeSpan.FromSeconds(15);   // a request that stops moving is failed long before the platform's 60 s deadline
    private static readonly TimeSpan MaxRetryAfterWait = TimeSpan.FromSeconds(5);    // a longer Retry-After is reported as a failure instead of leaving Explorer waiting
    private static readonly HttpClient s_download = CreateDownloadClient();
    // Windows re-issues a failed FETCH_DATA; remembering a recent network failure per item makes the retry fail instantly instead of waiting out the watchdog again.
    private static readonly ConcurrentDictionary<string, DateTime> s_recentFailures = new();
    private static readonly TimeSpan FailureMemory = TimeSpan.FromSeconds(30);

    private static HttpClient CreateDownloadClient()
    {
        var c = new HttpClient(new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30), PooledConnectionLifetime = TimeSpan.FromMinutes(5), MaxConnectionsPerServer = 8 }) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ISV|NoDrive|NoDrive/0.0.1-spike");
        return c;
    }

    private static CF_CONNECTION_KEY s_connection;
    private static HttpClient? s_graph;
    private static int s_maxConcurrent = 3;

    public static int QueueDepth => s_queue.Reader.Count;
    public static IReadOnlyCollection<double> CallbackMicros => s_callbackMicros;

    public static unsafe void Connect(string rootPath, HttpClient graph, int maxConcurrentHydrations)
    {
        s_graph = graph;
        s_maxConcurrent = maxConcurrentHydrations;

        var table = stackalloc CF_CALLBACK_REGISTRATION[6];
        table[0] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = &OnFetchData };
        table[1] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA, Callback = &OnCancelFetchData };
        table[2] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE, Callback = &OnNotifyDelete };
        table[3] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_RENAME, Callback = &OnNotifyRename };
        table[4] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS, Callback = &OnFetchPlaceholders };
        table[5] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE, Callback = null };

        fixed (char* p = rootPath)
        {
            CF_CONNECTION_KEY key;
            HRESULT hr = PInvoke.CfConnectSyncRoot(new PCWSTR(p), table, null,
                CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_BLOCK_SELF_IMPLICIT_HYDRATION, &key);
            Marshal.ThrowExceptionForHR(hr.Value);
            s_connection = key;
        }

        for (int i = 0; i < s_maxConcurrent; i++)
        {
            _ = Task.Run(WorkerAsync);
        }
        Console.WriteLine($"connected: key={s_connection}, {s_maxConcurrent} hydration workers");
    }

    public static void Disconnect()
    {
        if (s_connection.Value != 0)
        {
            HRESULT hr = PInvoke.CfDisconnectSyncRoot(s_connection);
            Console.WriteLine($"CfDisconnectSyncRoot -> 0x{hr.Value:X8}");
            s_connection = default;
        }
    }

    // ---------------- callbacks: validate, enqueue, return ----------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        long t0 = Stopwatch.GetTimestamp();
        ref var fd = ref parameters->FetchData;
        var req = new HydrationRequest(
            info->ConnectionKey.Value,
            info->TransferKey,
            info->RequestKey,
            IdentityToString(info->FileIdentity, info->FileIdentityLength),
            info->NormalizedPath.ToString(),
            info->FileSize,
            fd.RequiredFileOffset,
            fd.RequiredLength,
            fd.OptionalFileOffset,
            fd.OptionalLength,
            (uint)fd.Flags,
            info->PriorityHint,
            info->ProcessInfo is null ? null : info->ProcessInfo->ImagePath.ToString());

        s_inFlight[req.TransferKey] = new CancellationTokenSource();
        if (!s_queue.Writer.TryWrite(req))
        {
            // Queue full: fail fast rather than block the platform thread.
            Complete(req, CompletionStatus: NTSTATUS.STATUS_CLOUD_FILE_REQUEST_ABORTED);
        }
        RecordLatency(t0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnFetchPlaceholders(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        long t0 = Stopwatch.GetTimestamp();
        // The sync root itself has no FileIdentity; every other folder carries the Graph item id we stored when creating its placeholder.
        string? folderId = info->FileIdentityLength == 0 ? IndexRootId : IdentityToString(info->FileIdentity, info->FileIdentityLength);
        var req = new PopulateRequest(info->ConnectionKey.Value, info->TransferKey, info->RequestKey, folderId, info->NormalizedPath.ToString(), t0);
        if (!s_populateQueue.Writer.TryWrite(req))
        {
            CompletePopulate(req, NTSTATUS.STATUS_CLOUD_FILE_REQUEST_ABORTED);
        }
        RecordLatency(t0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnCancelFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        long t0 = Stopwatch.GetTimestamp();
        if (s_inFlight.TryGetValue(info->TransferKey, out var cts)) { cts.Cancel(); }
        RecordLatency(t0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnNotifyDelete(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        long t0 = Stopwatch.GetTimestamp();
        var op = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE,
            ConnectionKey = info->ConnectionKey,
            TransferKey = info->TransferKey,
            RequestKey = info->RequestKey,
        };
        var p = new CF_OPERATION_PARAMETERS { ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS) };
        p.AckDelete.Flags = CF_OPERATION_ACK_DELETE_FLAGS.CF_OPERATION_ACK_DELETE_FLAG_NONE;
        p.AckDelete.CompletionStatus = NTSTATUS.STATUS_SUCCESS;
        HRESULT hr = PInvoke.CfExecute(&op, &p);
        Console.WriteLine($"[cb] NOTIFY_DELETE {info->NormalizedPath} flags={parameters->Delete.Flags} -> ack 0x{hr.Value:X8} (would journal a remote delete)");
        RecordLatency(t0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnNotifyRename(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        long t0 = Stopwatch.GetTimestamp();
        var op = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME,
            ConnectionKey = info->ConnectionKey,
            TransferKey = info->TransferKey,
            RequestKey = info->RequestKey,
        };
        var p = new CF_OPERATION_PARAMETERS { ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS) };
        p.AckRename.Flags = CF_OPERATION_ACK_RENAME_FLAGS.CF_OPERATION_ACK_RENAME_FLAG_NONE;
        p.AckRename.CompletionStatus = NTSTATUS.STATUS_SUCCESS;
        HRESULT hr = PInvoke.CfExecute(&op, &p);
        Console.WriteLine($"[cb] NOTIFY_RENAME {info->NormalizedPath} -> {parameters->Rename.TargetPath} flags={parameters->Rename.Flags} -> ack 0x{hr.Value:X8}");
        RecordLatency(t0);
    }

    private static void RecordLatency(long t0)
    {
        s_callbackMicros.Add(Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
    }

    private static unsafe string IdentityToString(void* identity, uint length)
        => identity is null || length == 0 ? "" : Encoding.UTF8.GetString((byte*)identity, (int)length);

    // ---------------- FETCH_PLACEHOLDERS worker: answers from the index, never from the network ----------------

    private static async Task PopulateWorkerAsync()
    {
        await foreach (var req in s_populateQueue.Reader.ReadAllAsync())
        {
            var sw = Stopwatch.StartNew();
            double queueMs = Stopwatch.GetElapsedTime(req.EnqueuedTicks).TotalMilliseconds;
            try
            {
                long total; long sent; int batches;
                lock (s_indexLock)
                {
                    total = s_index!.CountChildren(req.FolderId!);
                    sent = PlaceholderWriter.Transfer(req.ConnectionKey, req.TransferKey, req.RequestKey, s_index.EnumerateChildren(req.FolderId!), total, 1000, out batches);
                }
                using var p = Process.GetCurrentProcess();
                Console.WriteLine($"[populate] folder={req.FolderId} children={total} sent={sent} batches={batches} queued={queueMs:F1} ms total={sw.Elapsed.TotalMilliseconds:F0} ms private={p.PrivateMemorySize64 / 1048576.0:F1} MB");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[populate] FAILED folder={req.FolderId}: {ex.GetType().Name} {ex.Message}");
                CompletePopulate(req, NTSTATUS.STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE);
            }
        }
    }

    /// <summary>Fails a FETCH_PLACEHOLDERS request with a status so Explorer shows an error instead of waiting for the 60 s deadline.</summary>
    private static unsafe void CompletePopulate(PopulateRequest req, NTSTATUS status)
    {
        var op = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
            ConnectionKey = new CF_CONNECTION_KEY(req.ConnectionKey),
            TransferKey = req.TransferKey,
            RequestKey = req.RequestKey,
        };
        var prm = new CF_OPERATION_PARAMETERS { ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS) };
        prm.TransferPlaceholders.Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_NONE;
        prm.TransferPlaceholders.CompletionStatus = status;
        prm.TransferPlaceholders.PlaceholderTotalCount = 0;
        prm.TransferPlaceholders.PlaceholderArray = null;
        prm.TransferPlaceholders.PlaceholderCount = 0;
        HRESULT hr = PInvoke.CfExecute(&op, &prm);
        Console.WriteLine($"[populate] completed folder={req.FolderId} with status 0x{status.Value:X8} -> 0x{hr.Value:X8}");
    }

    // ---------------- worker ----------------

    private static async Task WorkerAsync()
    {
        await foreach (var req in s_queue.Reader.ReadAllAsync())
        {
            var cts = s_inFlight.GetValueOrDefault(req.TransferKey) ?? new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            Console.WriteLine($"[hydrate] item={req.ItemId} required=[{req.Offset}+{req.Length}] optional=[{req.OptOffset}+{req.OptLength}] size={req.FileSize} flags=0x{req.Flags:X} prio={req.Priority} by={System.IO.Path.GetFileName(req.Process ?? "?")}");
            try
            {
                if (s_recentFailures.TryGetValue(req.ItemId, out var failedAt) && DateTime.UtcNow - failedAt < FailureMemory)
                {
                    throw new IOException($"item failed {(DateTime.UtcNow - failedAt).TotalSeconds:F0} s ago; failing fast (retry window {FailureMemory.TotalSeconds:F0} s)");
                }
                await HydrateAsync(req, cts.Token);
                s_recentFailures.TryRemove(req.ItemId, out _);
                Console.WriteLine($"[hydrate] done item={req.ItemId} {req.Length / 1048576.0:F1} MiB in {sw.Elapsed.TotalSeconds:F1} s");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[hydrate] cancelled {req.Path}");
                Complete(req, NTSTATUS.STATUS_CLOUD_FILE_REQUEST_ABORTED);
            }
            catch (Exception ex)
            {
                // Remember only failures where an immediate retry is pointless (no progress, throttling, not found). A dropped connection is transient: Windows re-requests the missing range and it can succeed.
                if (ex is TimeoutException or HttpRequestException) { s_recentFailures[req.ItemId] = DateTime.UtcNow; }
                Console.WriteLine($"[hydrate] FAILED {req.Path}: {ex.GetType().Name} {ex.Message}");
                Complete(req, NTSTATUS.STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE);
            }
            finally
            {
                s_inFlight.TryRemove(req.TransferKey, out _);
            }
        }
    }

    private static async Task HydrateAsync(HydrationRequest req, CancellationToken ct)
    {
        string url = s_downloadBase is not null
            ? $"{s_downloadBase}/download/{Uri.EscapeDataString(req.ItemId)}?fault={s_fault}"
            : await GetDownloadUrlAsync(req.ItemId, ct);
        long end = req.Offset + req.Length; // exclusive

        // Watchdog (design rule 2): every await below is bounded by "no progress for 20 s"; the timer is re-armed after each chunk.
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(NoProgressTimeout);
        try
        {
            await HydrateCoreAsync(req, url, end, watchdog, ct);
        }
        catch (OperationCanceledException) when (watchdog.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no progress for {NoProgressTimeout.TotalSeconds:F0} s, failing the request instead of leaving the caller waiting");
        }
    }

    private static async Task HydrateCoreAsync(HydrationRequest req, string url, long end, CancellationTokenSource watchdog, CancellationToken ct)
    {
        var token = watchdog.Token;
        HttpResponseMessage resp;
        int attempt = 0;
        while (true)
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, url);
            msg.Headers.Range = new RangeHeaderValue(req.Offset, end - 1);
            resp = await s_download.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)resp.StatusCode is 429 or 503)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                resp.Dispose();
                if (wait > MaxRetryAfterWait || ++attempt > 2)
                {
                    throw new HttpRequestException($"throttled: Retry-After {wait.TotalSeconds:F0} s exceeds the {MaxRetryAfterWait.TotalSeconds:F0} s budget; failing the request now (attempt {attempt})");
                }
                Console.WriteLine($"[hydrate] throttled, waiting Retry-After {wait.TotalSeconds:F0} s (attempt {attempt})");
                await Task.Delay(wait, token);
                watchdog.CancelAfter(NoProgressTimeout);
                continue;
            }
            break;
        }
        using var response = resp;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var http = response;   // keeps the status check below readable

        long skip = 0;
        if (http.StatusCode == System.Net.HttpStatusCode.OK && req.Offset > 0)
        {
            skip = req.Offset; // Range ignored: server sent the whole file; discard the prefix.
            Console.WriteLine($"[hydrate] server ignored Range (200); skipping {skip} bytes");
        }

        const int chunkSize = 1024 * 1024;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
        try
        {
            while (skip > 0)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(chunkSize, skip)), token);
                watchdog.CancelAfter(NoProgressTimeout);
                if (n == 0) { throw new IOException("stream ended while skipping"); }
                skip -= n;
            }

            long position = req.Offset;
            while (position < end)
            {
                int want = (int)Math.Min(chunkSize, end - position);
                int filled = 0;
                while (filled < want)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(filled, want - filled), token);
                    watchdog.CancelAfter(NoProgressTimeout);
                    if (n == 0) { break; }
                    filled += n;
                }
                if (filled == 0) { throw new IOException($"stream ended at {position}, expected {end}"); }
                TransferData(req, buffer, position, filled);
                position += filled;
                PInvoke.CfReportProviderProgress(new CF_CONNECTION_KEY(req.ConnectionKey), req.TransferKey, req.Length, position - req.Offset);
                if (((position - req.Offset) & ((64L << 20) - 1)) < chunkSize) { Console.WriteLine($"[hydrate] item={req.ItemId} {(position - req.Offset) / 1048576} / {req.Length / 1048576} MiB"); }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string> GetDownloadUrlAsync(string itemId, CancellationToken ct)
    {
        if (s_downloadUrls.TryGetValue(itemId, out var cached) && DateTime.UtcNow - cached.FetchedUtc < TimeSpan.FromMinutes(45))
        {
            return cached.Url;
        }
        string url = await GraphLite.GetDownloadUrlAsync(s_graph!, itemId, ct);
        s_downloadUrls[itemId] = (url, DateTime.UtcNow);
        return url;
    }

    private static unsafe void TransferData(HydrationRequest req, byte[] buffer, long offset, int length)
    {
        fixed (byte* p = buffer)
        {
            var op = new CF_OPERATION_INFO
            {
                StructSize = (uint)sizeof(CF_OPERATION_INFO),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                ConnectionKey = new CF_CONNECTION_KEY(req.ConnectionKey),
                TransferKey = req.TransferKey,
                RequestKey = req.RequestKey,
            };
            var prm = new CF_OPERATION_PARAMETERS { ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS) };
            prm.TransferData.Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE;
            prm.TransferData.CompletionStatus = NTSTATUS.STATUS_SUCCESS;
            prm.TransferData.Buffer = p;
            prm.TransferData.Offset = offset;
            prm.TransferData.Length = length;
            HRESULT hr = PInvoke.CfExecute(&op, &prm);
            if (hr.Failed) { throw new InvalidOperationException($"CfExecute(TRANSFER_DATA) at {offset}+{length} failed 0x{hr.Value:X8}"); }
        }
    }

    /// <summary>Completes a hydration request with a failure status so Explorer shows an error instead of waiting 60 s.</summary>
    private static unsafe void Complete(HydrationRequest req, NTSTATUS CompletionStatus)
    {
        var op = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = new CF_CONNECTION_KEY(req.ConnectionKey),
            TransferKey = req.TransferKey,
            RequestKey = req.RequestKey,
        };
        var prm = new CF_OPERATION_PARAMETERS { ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS) };
        prm.TransferData.Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE;
        prm.TransferData.CompletionStatus = CompletionStatus;
        prm.TransferData.Buffer = null;
        prm.TransferData.Offset = req.Offset;
        prm.TransferData.Length = req.Length;
        HRESULT hr = PInvoke.CfExecute(&op, &prm);
        Console.WriteLine($"[hydrate] completed {req.Path} with status 0x{CompletionStatus.Value:X8} -> 0x{hr.Value:X8}");
    }
}
