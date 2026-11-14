using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;

namespace NoDrive.Spikes.CfApiMinimal;

internal sealed record RemoteItem(string Id, string Name, string? ParentId, bool IsFolder, long Size, DateTimeOffset LastModified, DateTimeOffset Created);

internal static unsafe class PlaceholderWriter
{
    /// <summary>Creates placeholders for <paramref name="children"/> directly under <paramref name="directory"/>. Returns the count created.</summary>
    public static int CreateChildren(string directory, IReadOnlyList<RemoteItem> children)
    {
        if (children.Count == 0) { return 0; }

        var entries = (CF_PLACEHOLDER_CREATE_INFO*)NativeMemory.AllocZeroed((nuint)children.Count, (nuint)sizeof(CF_PLACEHOLDER_CREATE_INFO));
        var allocations = new List<nint>(children.Count * 2);
        try
        {
            for (int i = 0; i < children.Count; i++)
            {
                var item = children[i];
                nint name = Marshal.StringToHGlobalUni(item.Name);
                allocations.Add(name);
                byte[] idBytes = Encoding.UTF8.GetBytes(item.Id);
                nint id = Marshal.AllocHGlobal(idBytes.Length);
                Marshal.Copy(idBytes, 0, id, idBytes.Length);
                allocations.Add(id);

                entries[i] = new CF_PLACEHOLDER_CREATE_INFO
                {
                    RelativeFileName = new PCWSTR((char*)name),
                    FsMetadata = new CF_FS_METADATA
                    {
                        FileSize = item.IsFolder ? 0 : item.Size,
                        BasicInfo = new FILE_BASIC_INFO
                        {
                            CreationTime = item.Created.ToFileTime(),
                            LastWriteTime = item.LastModified.ToFileTime(),
                            LastAccessTime = item.LastModified.ToFileTime(),
                            ChangeTime = item.LastModified.ToFileTime(),
                            FileAttributes = item.IsFolder
                                ? (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY
                                : (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
                        },
                    },
                    FileIdentity = (void*)id,
                    FileIdentityLength = (uint)idBytes.Length,
                    // Population policy is ALWAYS_FULL in this spike, so directories are created fully populated by us.
                    Flags = item.IsFolder
                        ? CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC | CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION
                        : CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
                };
            }

            uint processed = 0;
            fixed (char* pDir = directory)
            {
                HRESULT hr = PInvoke.CfCreatePlaceholders(new PCWSTR(pDir), entries, (uint)children.Count, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, &processed);
                if (hr.Failed)
                {
                    Console.WriteLine($"CfCreatePlaceholders({directory}) -> 0x{hr.Value:X8}, processed {processed}/{children.Count}");
                }
            }

            int ok = 0;
            for (int i = 0; i < children.Count; i++)
            {
                if (entries[i].Result.Succeeded) { ok++; }
                else { Console.WriteLine($"  placeholder '{children[i].Name}' failed 0x{entries[i].Result.Value:X8}"); }
            }
            return ok;
        }
        finally
        {
            foreach (var a in allocations) { Marshal.FreeHGlobal(a); }
            NativeMemory.Free(entries);
        }
    }
}
