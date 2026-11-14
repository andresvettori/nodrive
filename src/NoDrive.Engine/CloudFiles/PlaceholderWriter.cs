using System.Runtime.InteropServices;
using System.Text;
using NoDrive.Core.Model;
using NoDrive.Engine.Infrastructure;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;

namespace NoDrive.Engine.CloudFiles;

/// <summary>
/// Creates placeholders two ways. (1) Answering a FETCH_PLACEHOLDERS callback with CfExecute(TRANSFER_PLACEHOLDERS): the completion flag
/// travels with the LAST real batch (an extra empty call after the total is reached is rejected, P0). (2) Background population with
/// CfCreatePlaceholders + CfUpdatePlaceholder(DISABLE_ON_DEMAND_POPULATION), which keeps the cost (about 160 us per placeholder inside
/// Windows, not tunable) off the user's path. FileIdentity of every placeholder is the Graph item id (UTF-8).
/// </summary>
internal static unsafe class PlaceholderWriter
{
    public const int DefaultBatch = 1000;

    public static long Transfer(long connectionKey, long transferKey, long requestKey, IEnumerable<IndexedItem> children, long total, int batchSize = DefaultBatch)
    {
        long sent = 0;
        var batch = new List<IndexedItem>(batchSize);
        List<IndexedItem>? held = null;
        foreach (var c in children)
        {
            batch.Add(c);
            if (batch.Count < batchSize) { continue; }
            if (held is not null) { sent += held.Count; TransferBatch(connectionKey, transferKey, requestKey, held, total, last: false); }
            held = batch;
            batch = new List<IndexedItem>(batchSize);
        }
        if (batch.Count > 0)
        {
            if (held is not null) { sent += held.Count; TransferBatch(connectionKey, transferKey, requestKey, held, total, last: false); }
            held = batch;
        }
        held ??= [];                                  // empty folder: one empty batch completes the request
        sent += held.Count;
        TransferBatch(connectionKey, transferKey, requestKey, held, total, last: true);
        return sent;
    }

    private static void TransferBatch(long connectionKey, long transferKey, long requestKey, List<IndexedItem> items, long total, bool last)
    {
        var entries = (CF_PLACEHOLDER_CREATE_INFO*)NativeMemory.AllocZeroed((nuint)Math.Max(1, items.Count), (nuint)sizeof(CF_PLACEHOLDER_CREATE_INFO));
        var allocations = new List<nint>(items.Count * 2);
        try
        {
            for (int i = 0; i < items.Count; i++) { entries[i] = BuildEntry(items[i], allocations); }
            var op = new CF_OPERATION_INFO
            {
                StructSize = (uint)sizeof(CF_OPERATION_INFO),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
                ConnectionKey = new CF_CONNECTION_KEY(connectionKey),
                TransferKey = transferKey,
                RequestKey = requestKey,
            };
            var prm = new CF_OPERATION_PARAMETERS { ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS) };
            prm.TransferPlaceholders.Flags = last
                ? CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION
                : CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_NONE;
            prm.TransferPlaceholders.CompletionStatus = NTSTATUS.STATUS_SUCCESS;
            prm.TransferPlaceholders.PlaceholderTotalCount = total;
            prm.TransferPlaceholders.PlaceholderArray = entries;
            prm.TransferPlaceholders.PlaceholderCount = (uint)items.Count;
            HRESULT hr = PInvoke.CfExecute(&op, &prm);
            if (hr.Failed) { throw new InvalidOperationException($"CfExecute(TRANSFER_PLACEHOLDERS) failed 0x{hr.Value:X8} (batch of {items.Count}, processed {prm.TransferPlaceholders.EntriesProcessed})"); }
        }
        finally
        {
            foreach (var a in allocations) { Marshal.FreeHGlobal(a); }
            NativeMemory.Free(entries);
        }
    }

    /// <summary>Background population of one directory (does not mark it populated: call <see cref="MarkPopulated"/> when all batches are in).</summary>
    public static int CreateChildren(string directory, IReadOnlyList<IndexedItem> children)
    {
        if (children.Count == 0) { return 0; }
        var entries = (CF_PLACEHOLDER_CREATE_INFO*)NativeMemory.AllocZeroed((nuint)children.Count, (nuint)sizeof(CF_PLACEHOLDER_CREATE_INFO));
        var allocations = new List<nint>(children.Count * 2);
        try
        {
            for (int i = 0; i < children.Count; i++) { entries[i] = BuildEntry(children[i], allocations); }
            uint processed = 0;
            fixed (char* pDir = directory)
            {
                HRESULT hr = PInvoke.CfCreatePlaceholders(new PCWSTR(pDir), entries, (uint)children.Count, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, &processed);
                if (hr.Failed) { Log.Warn($"CfCreatePlaceholders({directory}) -> 0x{hr.Value:X8}, processed {processed}/{children.Count}"); }
            }
            int ok = 0;
            for (int i = 0; i < children.Count; i++)
            {
                if (entries[i].Result.Succeeded) { ok++; }
                else if (entries[i].Result.Value != unchecked((int)0x800700B7)) { Log.Warn($"placeholder '{children[i].Name}' in {directory} failed 0x{entries[i].Result.Value:X8}"); }   // 0x800700B7 = already exists
            }
            return ok;
        }
        finally
        {
            foreach (var a in allocations) { Marshal.FreeHGlobal(a); }
            NativeMemory.Free(entries);
        }
    }

    /// <summary>Tells Windows the directory is fully populated: it will not call FETCH_PLACEHOLDERS for it any more.</summary>
    public static bool MarkPopulated(string directory)
    {
        HANDLE h = default;
        fixed (char* p = directory)
        {
            HRESULT open = PInvoke.CfOpenFileWithOplock(new PCWSTR(p), CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS, &h);
            if (open.Failed) { Log.Warn($"mark populated: open {directory} -> 0x{open.Value:X8}"); return false; }
        }
        try
        {
            HRESULT hr = PInvoke.CfUpdatePlaceholder(h, null, null, 0, null, 0, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DISABLE_ON_DEMAND_POPULATION | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC, null, null);
            if (hr.Failed) { Log.Warn($"mark populated: update {directory} -> 0x{hr.Value:X8}"); }
            return hr.Succeeded;
        }
        finally { PInvoke.CfCloseHandle(h); }
    }

    private static CF_PLACEHOLDER_CREATE_INFO BuildEntry(IndexedItem item, List<nint> allocations)
    {
        nint name = Marshal.StringToHGlobalUni(item.Name);
        allocations.Add(name);
        byte[] idBytes = Encoding.UTF8.GetBytes(item.Id);
        nint id = Marshal.AllocHGlobal(idBytes.Length);
        Marshal.Copy(idBytes, 0, id, idBytes.Length);
        allocations.Add(id);
        long when = new DateTime(item.ModifiedUtcTicks, DateTimeKind.Utc).ToFileTimeUtc();
        return new CF_PLACEHOLDER_CREATE_INFO
        {
            RelativeFileName = new PCWSTR((char*)name),
            FsMetadata = new CF_FS_METADATA
            {
                FileSize = item.IsFolder ? 0 : item.Size,
                BasicInfo = new FILE_BASIC_INFO
                {
                    CreationTime = when, LastWriteTime = when, LastAccessTime = when, ChangeTime = when,
                    FileAttributes = item.IsFolder ? (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY : (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
                },
            },
            FileIdentity = (void*)id,
            FileIdentityLength = (uint)idBytes.Length,
            // Folders stay on-demand (lazy) unless the prefetcher populates them; files start in sync with the cloud.
            Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
        };
    }
}
