using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using NoDrive.Core.Hydration;
using NoDrive.Core.Index;
using NoDrive.Engine.Infrastructure;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace NoDrive.Engine.CloudFiles;

/// <summary>Everything the callbacks need, injected once by <see cref="CloudProvider.Start"/>.</summary>
internal sealed record ProviderServices(ItemIndex Index, Hydrator Hydrator, PopulationState Population, FolderLocks Locks, CallerStats Callers, Func<bool> IndexReady, int HydrationWorkers, ReadAheadPlanner ReadAhead);

/// <summary>
/// The Cloud Files connection. Design rule 1 (never block Explorer): every callback only copies its request into a bounded channel and
/// returns in microseconds; workers do the network and disk work and complete the request through CfExecute. Every request is completed
/// (success or an explicit failure status), never left pending. M1 is read-only: deletes and renames are refused (no remote propagation yet).
/// </summary>
internal static class CloudProvider
{
    private sealed record DataRequest(long ConnectionKey, long TransferKey, long RequestKey, string ItemId, long FileSize, long Offset, long Length, long OptionalOffset, long OptionalLength, string? Process, bool Background, long EnqueuedTicks, string? NormPath = null);

    /// <summary>Files up to this size are downloaded whole at the first read by interactive apps (Photos cancels slow partial reads and reports the image as corrupted).</summary>
    private const long WholeFileLimit = 64L * 1024 * 1024;
    private static string s_driveRoot = "C:";
    private static readonly ConcurrentDictionary<string, byte> s_backfilling = new(StringComparer.Ordinal);

    /// <summary>Thrown when Windows refuses data BEYOND the required range (it may already have it): read-ahead stops quietly, the request itself succeeded.</summary>
    private sealed class ReadAheadStopped : Exception { }
    private sealed record PopulateRequest(long ConnectionKey, long TransferKey, long RequestKey, string FolderId, long EnqueuedTicks);

    private static ProviderServices s_svc = null!;
    private static CF_CONNECTION_KEY s_connection;
    // Two lanes: interactive apps are served first; Explorer thumbnails, search and antivirus reads (Background) never delay a player.
    private static readonly Channel<DataRequest> s_dataQueue = Channel.CreateBounded<DataRequest>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait });
    private static readonly Channel<DataRequest> s_backgroundQueue = Channel.CreateBounded<DataRequest>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest });
    private static readonly HashSet<string> BackgroundCallers = new(StringComparer.OrdinalIgnoreCase) { "explorer.exe", "SearchProtocolHost.exe", "SearchFilterHost.exe", "SearchIndexer.exe", "MsMpEng.exe", "dllhost.exe", "prevhost.exe" };
    private static readonly Channel<PopulateRequest> s_populateQueue = Channel.CreateBounded<PopulateRequest>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait });
    // Keyed by RequestKey (unique per request). TransferKey is shared by all requests on one open file, so it must never own a cancellation handle.
    private sealed record InFlight(long TransferKey, long Offset, long Length, CancellationTokenSource Cts);
    private static readonly ConcurrentDictionary<long, InFlight> s_inFlight = new();
    private static readonly ConcurrentBag<double> s_callbackMicros = [];
    private static readonly CancellationTokenSource s_stop = new();

    public static IReadOnlyCollection<double> CallbackMicros => s_callbackMicros;
    public static int Pending => s_dataQueue.Reader.Count + s_backgroundQueue.Reader.Count + s_populateQueue.Reader.Count;

    public static unsafe void Start(string rootPath, ProviderServices services)
    {
        s_svc = services;
        var table = stackalloc CF_CALLBACK_REGISTRATION[7];
        table[0] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = &OnFetchData };
        table[1] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA, Callback = &OnCancelFetchData };
        table[2] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS, Callback = &OnFetchPlaceholders };
        table[3] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE, Callback = &OnNotifyDelete };
        table[4] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_RENAME, Callback = &OnNotifyRename };
        table[5] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION, Callback = &OnFileOpenCompletion };
        table[6] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE, Callback = null };

        fixed (char* p = rootPath)
        {
            CF_CONNECTION_KEY key;
            HRESULT hr = PInvoke.CfConnectSyncRoot(new PCWSTR(p), table, null,
                CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_BLOCK_SELF_IMPLICIT_HYDRATION, &key);
            Marshal.ThrowExceptionForHR(hr.Value);
            s_connection = key;
        }
        for (int i = 0; i < services.HydrationWorkers; i++) { bool backgroundOnly = i == 0; _ = Task.Run(() => DataWorkerAsync(backgroundOnly)); }   // worker 0 only serves the background lane; the others take interactive work first
        for (int i = 0; i < 2; i++) { _ = Task.Run(PopulateWorkerAsync); }
        s_driveRoot = (Path.GetPathRoot(rootPath) ?? "C:\\").TrimEnd('\\');
        Log.Info($"connected to the sync root (key 0x{s_connection.Value:X}), {services.HydrationWorkers} hydration workers");
    }

    public static void Stop()
    {
        s_stop.Cancel();
        if (s_connection.Value != 0)
        {
            PInvoke.CfDisconnectSyncRoot(s_connection);
            s_connection = default;
        }
    }

    // ---------------- callbacks: validate, enqueue, return (microseconds) ----------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        long t0 = Stopwatch.GetTimestamp();
        var req = new DataRequest(info->ConnectionKey.Value, info->TransferKey, info->RequestKey, Identity(info), info->FileSize,
            parameters->FetchData.RequiredFileOffset, parameters->FetchData.RequiredLength, parameters->FetchData.OptionalFileOffset, parameters->FetchData.OptionalLength,
            info->ProcessInfo is null ? null : info->ProcessInfo->ImagePath.ToString(), false, t0, info->NormalizedPath.ToString());
        req = req with { Background = req.Process is not null && BackgroundCallers.Contains(Path.GetFileName(req.Process)) };
        try
        {
            s_inFlight[req.RequestKey] = new InFlight(req.TransferKey, req.Offset, req.Length, CancellationTokenSource.CreateLinkedTokenSource(s_stop.Token));
            if (req.ItemId.Length == 0 || !(req.Background ? s_backgroundQueue : s_dataQueue).Writer.TryWrite(req)) { CompleteData(req, NTSTATUS.STATUS_CLOUD_FILE_REQUEST_ABORTED); }
        }
        catch (Exception ex) { Log.Error($"FETCH_DATA callback: {ex.Message}"); }
        Record(t0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnCancelFetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            long from = parameters->Cancel.Anonymous.FetchData.FileOffset, to = from + parameters->Cancel.Anonymous.FetchData.Length;
            foreach (var e in s_inFlight.Values)
            {
                if (e.TransferKey != info->TransferKey || e.Offset + e.Length <= from || e.Offset >= to) { continue; }   // only the cancelled range
                try { e.Cts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
            }
        }
        catch (Exception ex) { Log.Error($"CANCEL callback: {ex.Message}"); }
        Record(t0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnFetchPlaceholders(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        long t0 = Stopwatch.GetTimestamp();
        // The sync root itself carries no FileIdentity; every other folder carries the Graph id stored when its placeholder was created.
        string folderId = info->FileIdentityLength == 0 ? s_svc.Population.RootId : Identity(info);
        var req = new PopulateRequest(info->ConnectionKey.Value, info->TransferKey, info->RequestKey, folderId, t0);
        if (!s_populateQueue.Writer.TryWrite(req)) { CompletePopulate(req, NTSTATUS.STATUS_CLOUD_FILE_REQUEST_ABORTED); }
        Record(t0);
    }

    /// <summary>
    /// A placeholder was opened (Explorer opens a file to pin or free it). No acknowledgement is needed; we only record a hint so the pin
    /// state is checked shortly afterwards (debounced), because CfSetPinState raises no file-system notification.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnFileOpenCompletion(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        long t0 = Stopwatch.GetTimestamp();
        string path = info->VolumeDosName.ToString() + info->NormalizedPath.ToString();
        if (path.Length > 3) { PinWatcher.Hint(path, TimeSpan.FromMilliseconds(1200)); }
        Record(t0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnNotifyDelete(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        long t0 = Stopwatch.GetTimestamp();
        // Acknowledge at once (a move/delete dialog must finish at local speed); the cloud catches up from the journal.
        if (!IsSelf(info)) { LocalOps.Post("delete", Identity(info), null); }
        Ack(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE, NTSTATUS.STATUS_SUCCESS);
        Record(t0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe void OnNotifyRename(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        long t0 = Stopwatch.GetTimestamp();
        if (!IsSelf(info)) { LocalOps.Post("rename", Identity(info), parameters->Rename.TargetPath.ToString()); }
        Ack(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME, NTSTATUS.STATUS_SUCCESS);
        Record(t0);
    }

    private static unsafe bool IsSelf(CF_CALLBACK_INFO* info) => info->ProcessInfo is not null && info->ProcessInfo->ProcessId == (uint)Environment.ProcessId;

    private static unsafe string Identity(CF_CALLBACK_INFO* info) =>
        info->FileIdentity is null || info->FileIdentityLength == 0 ? "" : Encoding.UTF8.GetString((byte*)info->FileIdentity, (int)info->FileIdentityLength);

    private static void Record(long t0) => s_callbackMicros.Add(Stopwatch.GetElapsedTime(t0).TotalMicroseconds);

    private static unsafe void Ack(CF_CALLBACK_INFO* info, CF_OPERATION_TYPE type, NTSTATUS status)
    {
        var op = new CF_OPERATION_INFO { StructSize = (uint)sizeof(CF_OPERATION_INFO), Type = type, ConnectionKey = info->ConnectionKey, TransferKey = info->TransferKey, RequestKey = info->RequestKey };
        var p = new CF_OPERATION_PARAMETERS { ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS) };
        if (type == CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE) { p.AckDelete.CompletionStatus = status; }
        else { p.AckRename.CompletionStatus = status; }
        PInvoke.CfExecute(&op, &p);
    }

    // ---------------- hydration worker ----------------

    private static async Task<DataRequest> NextAsync(bool backgroundOnly)
    {
        while (true)
        {
            s_stop.Token.ThrowIfCancellationRequested();
            DataRequest? r;
            if (!backgroundOnly && s_dataQueue.Reader.TryRead(out r)) { return r; }
            if (s_backgroundQueue.Reader.TryRead(out r)) { return r; }
            var waits = backgroundOnly
                ? new[] { s_backgroundQueue.Reader.WaitToReadAsync(s_stop.Token).AsTask() }
                : new[] { s_dataQueue.Reader.WaitToReadAsync(s_stop.Token).AsTask(), s_backgroundQueue.Reader.WaitToReadAsync(s_stop.Token).AsTask() };
            await Task.WhenAny(waits).ConfigureAwait(false);
        }
    }

    private static async Task DataWorkerAsync(bool backgroundOnly)
    {
        while (!s_stop.IsCancellationRequested)
        {
            DataRequest req;
            try { req = await NextAsync(backgroundOnly).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            double queuedMs = Stopwatch.GetElapsedTime(req.EnqueuedTicks).TotalMilliseconds;
            if (queuedMs > 1000) { Log.Info($"request for {Path.GetFileName(req.Process ?? "?")} waited {queuedMs:F0} ms in the {(req.Background ? "background" : "interactive")} queue"); }
            var cts = s_inFlight.GetValueOrDefault(req.RequestKey)?.Cts ?? new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            s_svc.Callers.Record(req.Process, req.Length, req.FileSize);
            ActiveTransfer? act = null;
            try
            {
                // Read-ahead (P1 finding): a player reading 40 KB at a time pays a network round trip per read; fetch a growing window beyond the required range.
                bool whole = !req.Background && req.FileSize > 0 && req.FileSize <= WholeFileLimit;
                long fetch = whole ? req.FileSize : req.Background ? req.Length : s_svc.ReadAhead.Plan(req.ItemId, req.Offset, req.Length, req.OptionalOffset, req.OptionalLength, req.FileSize);   // thumbnails/search/AV: exactly what they asked, never ahead
                s_svc.Callers.RecordFetched(req.Process, fetch);
                if (!req.Background) { act = ActivityLog.Begin("download", Path.GetFileName(req.NormPath) is { Length: > 0 } nm ? nm : req.ItemId, whole ? req.FileSize : fetch); }
                try
                {
                    await s_svc.Hydrator.StreamAsync(req.ItemId, whole ? 0 : req.Offset, fetch, whole ? 0 : req.Length,
                        (chunk, offset) => { if (!whole && offset >= req.Offset + req.Length && (s_dataQueue.Reader.Count > 0 || s_backgroundQueue.Reader.Count > 8)) { throw new ReadAheadStopped(); } TransferData(req, chunk, offset); },
                        done => { if (act is not null) { act.Done = done; } PInvoke.CfReportProviderProgress(new CF_CONNECTION_KEY(req.ConnectionKey), req.TransferKey, req.Length, Math.Min(done, req.Length)); },
                        cts.Token, req.FileSize).ConfigureAwait(false);
                }
                catch (ReadAheadStopped) { /* the required part was delivered; nothing more to do */ }
                if (act is not null) { ActivityLog.End(act, "ok"); act = null; }
                if (sw.ElapsedMilliseconds > 3000) { Log.Info($"hydrated {req.ItemId} [{req.Offset}+{req.Length}] for {Path.GetFileName(req.Process ?? "?")} in {sw.Elapsed.TotalSeconds:F1} s"); }
            }
            catch (OperationCanceledException)
            {
                if (act is not null) { ActivityLog.End(act, "cancelled", "the application stopped reading"); act = null; }
                if (!req.Background && req.FileSize > 0 && req.FileSize <= WholeFileLimit && !s_stop.IsCancellationRequested) { Backfill(req); }
                CompleteData(req, NTSTATUS.STATUS_CLOUD_FILE_REQUEST_ABORTED);
            }
            catch (Exception ex)
            {
                if (act is not null) { ActivityLog.End(act, "failed", ex.Message); act = null; }
                Log.Warn($"hydration of {req.ItemId} failed: {ex.Message}");
                CompleteData(req, NTSTATUS.STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE);
            }
            finally
            {
                if (s_inFlight.TryRemove(req.RequestKey, out var c)) { c.Cts.Dispose(); }
            }
        }
    }

    /// <summary>The caller gave up on its read (Photos does): finish the whole file in the background so the retry is served from disk.</summary>
    private static void Backfill(DataRequest req)
    {
        if (req.NormPath is not { Length: > 0 } rel || !s_backfilling.TryAdd(req.ItemId, 0)) { return; }
        string full = s_driveRoot + rel;
        _ = Task.Run(() =>
        {
            try { var hr = PinWatcher.Apply(full, hydrate: true); Log.Info($"background download of {Path.GetFileName(full)} after the caller cancelled: 0x{hr.Value:X8}"); }
            catch (Exception ex) { Log.Warn($"background download failed: {ex.Message}"); }
            finally { s_backfilling.TryRemove(req.ItemId, out _); }
        });
    }

    private static void TransferData(DataRequest req, ReadOnlySpan<byte> chunk, long offset)
    {
        unsafe
        {
        fixed (byte* p = chunk)
        {
            var op = new CF_OPERATION_INFO { StructSize = (uint)sizeof(CF_OPERATION_INFO), Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA, ConnectionKey = new CF_CONNECTION_KEY(req.ConnectionKey), TransferKey = req.TransferKey, RequestKey = req.RequestKey };
            var prm = new CF_OPERATION_PARAMETERS { ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS) };
            prm.TransferData.Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE;
            prm.TransferData.CompletionStatus = NTSTATUS.STATUS_SUCCESS;
            prm.TransferData.Buffer = p;
            prm.TransferData.Offset = offset;
            prm.TransferData.Length = chunk.Length;
            HRESULT hr = PInvoke.CfExecute(&op, &prm);
            if (hr.Succeeded) { ActivityLog.AddDown(chunk.Length); }
            if (hr.Failed)
            {
                if (offset >= req.Offset + req.Length) { throw new ReadAheadStopped(); }               // beyond the required range: not an error
                if (hr.Value == unchecked((int)0x8007018E)) { throw new OperationCanceledException(); }  // the caller cancelled (seek): normal
                throw new IOException($"CfExecute(TRANSFER_DATA) at {offset}+{chunk.Length} failed 0x{hr.Value:X8}");
            }
        }
        }
    }

    /// <summary>Fails the request with a status so Explorer shows an error immediately instead of waiting for the 60 s deadline.</summary>
    private static void CompleteData(DataRequest req, NTSTATUS status)
    {
        unsafe { CompleteDataCore(req, status); }
    }

    private static unsafe void CompleteDataCore(DataRequest req, NTSTATUS status)
    {
        var op = new CF_OPERATION_INFO { StructSize = (uint)sizeof(CF_OPERATION_INFO), Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA, ConnectionKey = new CF_CONNECTION_KEY(req.ConnectionKey), TransferKey = req.TransferKey, RequestKey = req.RequestKey };
        var prm = new CF_OPERATION_PARAMETERS { ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS) };
        prm.TransferData.Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE;
        prm.TransferData.CompletionStatus = status;
        prm.TransferData.Buffer = null;
        prm.TransferData.Offset = req.Offset;
        prm.TransferData.Length = req.Length;
        PInvoke.CfExecute(&op, &prm);
    }

    // ---------------- population worker (FETCH_PLACEHOLDERS answered from the index, never from the network) ----------------

    private static async Task PopulateWorkerAsync()
    {
        await foreach (var req in s_populateQueue.Reader.ReadAllAsync(s_stop.Token).ConfigureAwait(false))
        {
            if (!s_svc.IndexReady())
            {
                Log.Warn($"folder {req.FolderId} opened before the first sync finished; answering with an error (Explorer will show provider unavailable)");
                CompletePopulate(req, NTSTATUS.STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE);
                continue;
            }
            var sw = Stopwatch.StartNew();
            var gate = s_svc.Locks.For(req.FolderId);
            await gate.WaitAsync(s_stop.Token).ConfigureAwait(false);
            try
            {
                bool already = s_svc.Population.IsPopulated(req.FolderId);              // a background prefetch got there first: complete with nothing to add
                long total = already ? 0 : s_svc.Index.CountChildren(req.FolderId);
                long sent = PlaceholderWriter.Transfer(req.ConnectionKey, req.TransferKey, req.RequestKey, already ? [] : s_svc.Index.EnumerateChildren(req.FolderId), total);
                s_svc.Population.Set(req.FolderId, true);
                if (sw.ElapsedMilliseconds > 500 || total > 1000) { Log.Info($"populated {req.FolderId} on demand: {sent} placeholders in {sw.ElapsedMilliseconds} ms{(already ? " (already populated by prefetch)" : "")}"); }
            }
            catch (Exception ex)
            {
                Log.Warn($"populate {req.FolderId} failed: {ex.Message}");
                CompletePopulate(req, NTSTATUS.STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE);
            }
            finally { gate.Release(); }
        }
    }

    private static void CompletePopulate(PopulateRequest req, NTSTATUS status)
    {
        unsafe { CompletePopulateCore(req, status); }
    }

    private static unsafe void CompletePopulateCore(PopulateRequest req, NTSTATUS status)
    {
        var op = new CF_OPERATION_INFO { StructSize = (uint)sizeof(CF_OPERATION_INFO), Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS, ConnectionKey = new CF_CONNECTION_KEY(req.ConnectionKey), TransferKey = req.TransferKey, RequestKey = req.RequestKey };
        var prm = new CF_OPERATION_PARAMETERS { ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS) };
        prm.TransferPlaceholders.Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_NONE;
        prm.TransferPlaceholders.CompletionStatus = status;
        PInvoke.CfExecute(&op, &prm);
    }
}
