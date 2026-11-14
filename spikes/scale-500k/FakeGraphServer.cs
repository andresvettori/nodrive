using System.Net;
using System.Text;
using System.Text.Json;

namespace NoDrive.Spikes.Scale;

/// <summary>
/// Minimal fake of /me/drive/root/delta. Pages are generated on the fly (never stored) with the JSON shape and size measured on a
/// real personal drive (about 680 bytes per item). Layout: a 16-ary folder tree with about 12.7% folders (as on the real drive),
/// plus one folder holding 30,000 files. Second request round with ?since= returns a small change set (modify, add, delete).
/// </summary>
internal sealed class FakeGraphServer
{
    public const string DriveId = "F61C08DC8B03ABFF";
    private readonly int _items;
    private readonly int _pageSize;
    private readonly int _folders;
    private readonly int _bigFolder;
    private readonly int _bigFolderFiles;
    private readonly HttpListener _listener = new();
    private readonly Random _fault = new(42);
    public int Port { get; }

    public FakeGraphServer(int items, int pageSize, int port)
    {
        _items = items;
        _pageSize = pageSize;
        _folders = (int)(items * 0.127);
        _bigFolder = 7;
        _bigFolderFiles = Math.Min(30000, Math.Max(0, items - _folders) / 3);
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public int ExpectedBigFolderChildren => _bigFolderFiles + CountSubfolders(_bigFolder);
    public static string IdOf(int n) => $"{DriveId}!{n}";

    private int CountSubfolders(int folder)
    {
        int c = 0;
        for (int k = folder * 16 + 1; k <= folder * 16 + 16; k++) { if (k <= _folders) { c++; } }
        return c;
    }

    // Item n in [1, _items]: n <= _folders -> folder (parent = (n-1)/16, 0 = root); otherwise file.
    private int ParentOf(int n)
    {
        if (n <= _folders) { return (n - 1) / 16; }
        int fileIndex = n - _folders;                       // 1-based
        if (fileIndex <= _bigFolderFiles) { return _bigFolder; }
        return 1 + (int)((uint)(fileIndex * 2654435761u) % (uint)_folders);
    }

    public void Run(CancellationToken ct)
    {
        _listener.Start();
        Console.WriteLine($"fake graph: {_items} items, {_folders} folders, page size {_pageSize}, big folder #{_bigFolder} with {ExpectedBigFolderChildren} children, listening on {Port}");
        ct.Register(() => _listener.Stop());
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); } catch { break; }
            try { Handle(ctx); } catch (Exception ex) { Console.WriteLine("server error: " + ex.Message); try { ctx.Response.Abort(); } catch { } }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var q = ctx.Request.QueryString;
        int page = int.TryParse(q["page"], out var pg) ? pg : 0;
        bool incremental = q["since"] is not null;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        using var buffer = new MemoryStream(1 << 20);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            if (incremental) { WriteChanges(w); }
            else { WritePage(w, page); }
            w.WriteEndObject();
        }
        ctx.Response.ContentLength64 = buffer.Length;
        buffer.Position = 0;
        buffer.CopyTo(ctx.Response.OutputStream);
        ctx.Response.Close();
    }

    private void WritePage(Utf8JsonWriter w, int page)
    {
        int start = page * _pageSize + 1;
        int end = Math.Min(_items, start + _pageSize - 1);
        w.WriteStartArray("value");
        for (int n = start; n <= end; n++) { WriteItem(w, n, revision: 0); }
        w.WriteEndArray();
        if (end < _items) { w.WriteString("@odata.nextLink", $"http://127.0.0.1:{Port}/delta?page={page + 1}"); }
        else { w.WriteString("@odata.deltaLink", $"http://127.0.0.1:{Port}/delta?since=token1"); }
    }

    /// <summary>Incremental round: 3,000 modified files (new eTag/size), 1,000 new files in the big folder, 1,000 deleted files.</summary>
    private void WriteChanges(Utf8JsonWriter w)
    {
        w.WriteStartArray("value");
        int fileSpan = Math.Max(1, _items - _folders - _bigFolderFiles - 4000);   // keep the last 4,000 ids for deletions: no overlap
        for (int i = 0; i < 3000; i++) { WriteItem(w, _folders + 1 + _bigFolderFiles + (i * 13 % fileSpan), revision: 1); }
        for (int i = 0; i < 1000; i++) { WriteItem(w, _items + 1 + i, revision: 1, forcedParent: _bigFolder); }
        for (int i = 0; i < 1000; i++)
        {
            w.WriteStartObject();
            w.WriteString("id", IdOf(_items - i * 3));
            w.WriteStartObject("deleted"); w.WriteString("state", "deleted"); w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteString("@odata.deltaLink", $"http://127.0.0.1:{Port}/delta?since=token2");
    }

    private static readonly string[] s_words = ["Informe", "Documentación", "Factura", "Presupuesto", "Fotos", "Vacaciones", "Contrato", "Reunión", "Código", "Año"];
    private static readonly string[] s_ext = [".pdf", ".jpg", ".docx", ".xlsx", ".mp4", ".txt", ".png", ".zip"];

    private void WriteItem(Utf8JsonWriter w, int n, int revision, int? forcedParent = null)
    {
        bool folder = n <= _folders;
        int parent = forcedParent ?? ParentOf(n);
        string parentId = parent == 0 ? $"{DriveId}!101" : IdOf(parent);
        string word = s_words[n % s_words.Length];
        string name = folder ? $"{word} {n}" : $"{word} {n % 997} - {n}{s_ext[n % s_ext.Length]}";
        string guid = $"{n:X8}-0000-4000-8000-{revision:X12}";
        w.WriteStartObject();
        w.WriteString("createdDateTime", "2023-11-12T21:15:52Z");
        w.WriteString("eTag", $"\"{{{guid}}},{3 + revision}\"");
        w.WriteString("id", IdOf(n));
        w.WriteString("lastModifiedDateTime", revision == 0 ? "2024-10-19T11:38:49Z" : "2026-11-14T17:00:00Z");
        w.WriteString("name", name);
        w.WriteString("cTag", $"\"c:{{{guid}}},{2 + revision}\"");
        w.WriteNumber("size", folder ? 0 : 10_000 + (long)n * 37 % 50_000_000 + revision);
        w.WriteStartObject("parentReference");
        w.WriteString("driveId", DriveId.ToLowerInvariant());
        w.WriteString("driveType", "personal");
        w.WriteString("id", parentId);
        w.WriteString("path", "/drive/root:/Documentos/Carpeta " + (parent % 1000));
        w.WriteEndObject();
        if (folder)
        {
            w.WriteStartObject("folder"); w.WriteNumber("childCount", 16); w.WriteEndObject();
        }
        else
        {
            w.WriteStartObject("file");
            w.WriteString("mimeType", "application/octet-stream");
            w.WriteStartObject("hashes");
            w.WriteString("quickXorHash", "p3Du3rsvlHfuUVYHFcVzr73v9To=");
            w.WriteString("sha1Hash", "A845B6A9373087A2F47426CD5537316B20D821FF");
            w.WriteString("sha256Hash", "C51AEF994A15953169E528E350ACE191A24B035617238BA0E245F2E935AC2DF5");
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }
}
