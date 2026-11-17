using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;

namespace NoDrive.Engine.CloudFiles;

/// <summary>Reads the cloud item id stored in a placeholder's FileIdentity (used to recognise a renamed or moved placeholder after the engine was stopped).</summary>
internal static unsafe class PlaceholderInfo
{
    public static string? TryReadIdentity(string path)
    {
        HANDLE h;
        fixed (char* p = path)
        {
            // Attribute-only access with full sharing: works even while Explorer or another program has the file open.
            h = PInvoke.CreateFile(new PCWSTR(p), 0x80, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE, null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT, HANDLE.Null);
        }
        if (h.IsNull || (nint)h.Value == -1) { return null; }
        try
        {
            const int size = 1024;
            byte* buffer = stackalloc byte[size];
            uint returned;
            if (PInvoke.CfGetPlaceholderInfo(h, CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_BASIC, buffer, size, &returned).Failed) { return null; }
            var info = (CF_PLACEHOLDER_BASIC_INFO*)buffer;
            if (info->FileIdentityLength == 0 || info->FileIdentityLength > 512) { return null; }
            return Encoding.UTF8.GetString((byte*)&info->FileIdentity, (int)info->FileIdentityLength);
        }
        finally { PInvoke.CloseHandle(h); }
    }
}
