using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NoDrive.Engine.CloudFiles;
using NoDrive.Engine.Infrastructure;
using NoDrive.Engine.Shell;
using NoDrive.Engine.Switching;
using Windows.Win32;

namespace NoDrive.Engine.Web;

internal sealed class ActiveDto { public long Id { get; set; } public string Kind { get; set; } = ""; public string Name { get; set; } = ""; public long Total { get; set; } public long Done { get; set; } public long ElapsedMs { get; set; } }
internal sealed class SettingsDto
{
    public string Language { get; set; } = "auto";
    public int DownloadKiBps { get; set; }
    public int UploadKiBps { get; set; }
    public int PollActiveSeconds { get; set; }
    public int PollIdleMaxSeconds { get; set; }
    public int HydrationWorkers { get; set; }
    public string HydrationPolicy { get; set; } = "full";
}
internal sealed class CoexistenceDto { public bool OneDriveRunning { get; set; } public string? Email { get; set; } public string? Folder { get; set; } public List<string> BackedUp { get; set; } = []; public bool Inside { get; set; } }
internal sealed class StateDto
{
    public CoexistenceDto Coexistence { get; set; } = new();
    public string Version { get; set; } = "";
    public string Status { get; set; } = "";
    public bool Paused { get; set; }
    public bool NeedsSignIn { get; set; }
    public bool Autostart { get; set; }
    public int HeldDeletes { get; set; }
    public string RootPath { get; set; } = "";
    public string DataDir { get; set; } = "";
    public string ConfigPath { get; set; } = "";
    public string ClientId { get; set; } = "";
    public long Items { get; set; }
    public long PendingUploads { get; set; }
    public long PrivateBytes { get; set; }
    public long UptimeSeconds { get; set; }
    public long BytesDown { get; set; }
    public long BytesUp { get; set; }
    public string OsLanguage { get; set; } = "en";
    public string OsCulture { get; set; } = "en-US";
    public SettingsDto Settings { get; set; } = new();
    public List<ActiveDto> Active { get; set; } = [];
}
internal sealed class FolderDto { public string Folder { get; set; } = ""; }
internal sealed class SwitchDto
{
    public string Folder { get; set; } = "";
    public string? Email { get; set; }
    public bool OneDriveRunning { get; set; }
    public bool OneDriveLinked { get; set; }
    public List<string> BackedUp { get; set; } = [];
    public bool IndexReady { get; set; }
    public long PendingUploads { get; set; }
    public long PendingOps { get; set; }
    public bool Adopting { get; set; }
    public List<string> Blockers { get; set; } = [];
    public ScanResult Scan { get; set; } = new();
}
internal sealed class ActivityDto { public long LastId { get; set; } public List<ActivityEntry> Entries { get; set; } = []; }
internal sealed class SaveResultDto { public bool Ok { get; set; } public bool RestartRequired { get; set; } public string? Error { get; set; } }
internal sealed class FlagDto { public bool Value { get; set; } }

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StateDto))]
[JsonSerializable(typeof(ActivityDto))]
[JsonSerializable(typeof(SettingsDto))]
[JsonSerializable(typeof(SaveResultDto))]
[JsonSerializable(typeof(FlagDto))]
[JsonSerializable(typeof(FolderDto))]
[JsonSerializable(typeof(SwitchDto))]
internal sealed partial class WebJson : JsonSerializerContext { }

/// <summary>
/// The local settings and activity page (no UI framework in the always-on engine). Listens on localhost only, serves one embedded HTML file and a small JSON API.
/// Protection against other web pages and local users: random per-run token delivered once through the tray (cookie afterwards), Host header check
/// (DNS rebinding), and a custom header plus JSON content type on every write (forces a CORS preflight that is never granted).
/// Threading: handlers are short and never touch the network or the Cloud Files callbacks.
/// </summary>
internal sealed class WebUi(SyncService service, EngineConfig config, EnginePaths paths) : IDisposable
{
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly DateTime _started = DateTime.UtcNow;
    private HttpListener? _listener;
    private int _port;
    private string? _html;

    public string Url => $"http://localhost:{_port}/?t={_token}";

    public void Start(CancellationToken ct)
    {
        foreach (int candidate in new[] { config.WebPort > 0 ? config.WebPort : 47653, 0 })
        {
            int port = candidate == 0 ? FreePort() : candidate;
            var l = new HttpListener();
            l.Prefixes.Add($"http://localhost:{port}/");
            try { l.Start(); _listener = l; _port = port; break; }
            catch (HttpListenerException) { l.Close(); }
        }
        if (_listener is null) { Log.Warn("settings page unavailable: no local port could be opened"); return; }
        Log.Info($"settings page on http://localhost:{_port}/");
        _ = Task.Run(() => LoopAsync(ct), CancellationToken.None);
    }

    public void Open()
    {
        if (_listener is null) { return; }
        try { Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"could not open the browser: {ex.Message}"); }
    }

    public void Dispose() { try { _listener?.Close(); } catch (ObjectDisposedException) { } }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested || _listener is not { IsListening: true }) { return; }
            catch (HttpListenerException) { continue; }
            _ = Task.Run(() => HandleAsync(ctx), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            if (!string.Equals(req.Headers["Host"], $"localhost:{_port}", StringComparison.OrdinalIgnoreCase)) { Respond(ctx, 421, "text/plain", "Misdirected request"); return; }

            string path = req.Url?.AbsolutePath ?? "/";
            if (path == "/" && req.QueryString["t"] == _token)
            {
                ctx.Response.AppendHeader("Set-Cookie", $"nodrive={_token}; Path=/; HttpOnly; SameSite=Strict");
                ctx.Response.StatusCode = 302;
                ctx.Response.RedirectLocation = "/";
                ctx.Response.Close();
                return;
            }
            if (!HasCookie(req)) { Respond(ctx, 403, "text/html; charset=utf-8", "<h3>NoDrive</h3><p>Open this page from the NoDrive tray icon. / Abra esta página desde el icono de NoDrive en la bandeja.</p>"); return; }

            bool write = req.HttpMethod == "POST";
            if (write && (req.Headers["X-NoDrive"] != "1" || req.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)) { Respond(ctx, 400, "text/plain", "Bad request"); return; }

            switch (req.HttpMethod, path)
            {
                case ("GET", "/"): Respond(ctx, 200, "text/html; charset=utf-8", Html()); break;
                case ("GET", "/api/state"): Json(ctx, BuildState(), WebJson.Default.StateDto); break;
                case ("GET", "/api/activity"):
                    {
                        long after = long.TryParse(req.QueryString["after"], out long a) ? a : 0;
                        var (recent, _) = ActivityLog.Snapshot(after);
                        Json(ctx, new ActivityDto { LastId = recent.Count > 0 ? recent[^1].Id : after, Entries = recent }, WebJson.Default.ActivityDto);
                        break;
                    }
                case ("POST", "/api/settings"): Json(ctx, SaveSettings(await ReadAsync(ctx, WebJson.Default.SettingsDto).ConfigureAwait(false)), WebJson.Default.SaveResultDto); break;
                case ("POST", "/api/pause"): UserSettings.SetPaused((await ReadAsync(ctx, WebJson.Default.FlagDto).ConfigureAwait(false))?.Value ?? false); Json(ctx, new SaveResultDto { Ok = true }, WebJson.Default.SaveResultDto); break;
                case ("POST", "/api/autostart"): UserSettings.SetAutostart((await ReadAsync(ctx, WebJson.Default.FlagDto).ConfigureAwait(false))?.Value ?? false); Json(ctx, new SaveResultDto { Ok = true }, WebJson.Default.SaveResultDto); break;
                case ("GET", "/api/switch"): Json(ctx, BuildSwitch(req.QueryString["folder"]), WebJson.Default.SwitchDto); break;
                case ("POST", "/api/switch/scan"):
                    {
                        string f = (await ReadAsync(ctx, WebJson.Default.FolderDto).ConfigureAwait(false))?.Folder ?? "";
                        string? err = null;
                        bool ok = service.Assistant is { } a && a.StartScan(f, out err);
                        Json(ctx, new SaveResultDto { Ok = ok, Error = err }, WebJson.Default.SaveResultDto);
                        break;
                    }
                case ("POST", "/api/switch/takeover"):
                    {
                        string f = (await ReadAsync(ctx, WebJson.Default.FolderDto).ConfigureAwait(false))?.Folder ?? "";
                        string? err = null;
                        bool ok = service.Assistant is { } a && a.TakeOver(f, out err);
                        Json(ctx, new SaveResultDto { Ok = ok, Error = err }, WebJson.Default.SaveResultDto);
                        if (ok) { SwitchAssistant.RestartEngine(); _ = Task.Delay(800).ContinueWith(_ => TrayWindow.RequestQuit(), TaskScheduler.Default); }
                        break;
                    }
                case ("POST", "/api/signin"): service.RequestSignIn(); Json(ctx, new SaveResultDto { Ok = true }, WebJson.Default.SaveResultDto); break;
                case ("POST", "/api/allow-deletes"): service.AllowDeletes(); Json(ctx, new SaveResultDto { Ok = true }, WebJson.Default.SaveResultDto); break;
                case ("POST", "/api/open-folder"): OpenFolder(paths.SyncRoot); Json(ctx, new SaveResultDto { Ok = true }, WebJson.Default.SaveResultDto); break;
                case ("POST", "/api/open-data-folder"): OpenFolder(paths.DataDir); Json(ctx, new SaveResultDto { Ok = true }, WebJson.Default.SaveResultDto); break;
                default: Respond(ctx, 404, "text/plain", "Not found"); break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"settings page request failed: {ex.Message}");
            try { Respond(ctx, 500, "text/plain", "Error"); } catch (Exception) { /* connection already gone */ }
        }
    }

    private bool HasCookie(HttpListenerRequest req) => req.Cookies["nodrive"]?.Value == _token;

    private StateDto BuildState()
    {
        using var proc = Process.GetCurrentProcess();
        var (_, active) = ActivityLog.Snapshot(long.MaxValue);
        var now = DateTime.UtcNow;
        return new StateDto
        {
            Version = SyncRootRegistrar.ProviderVersion,
            Status = service.Status,
            Paused = UserSettings.Paused,
            NeedsSignIn = service.NeedsSignIn,
            Autostart = UserSettings.AutostartEnabled,
            HeldDeletes = service.HeldDeletes,
            RootPath = paths.SyncRoot,
            DataDir = paths.DataDir,
            ConfigPath = paths.ConfigPath,
            ClientId = config.ClientId,
            Items = service.ItemCount,
            PendingUploads = service.PendingUploads,
            PrivateBytes = proc.PrivateMemorySize64,
            UptimeSeconds = (long)(now - _started).TotalSeconds,
            BytesDown = ActivityLog.BytesDown,
            BytesUp = ActivityLog.BytesUp,
            Coexistence = ToDto(Infrastructure.Coexistence.Detect(paths.SyncRoot)),
            OsLanguage = OsLanguage(),
            OsCulture = OsCulture(),
            Settings = new SettingsDto { Language = config.Language, DownloadKiBps = config.DownloadKiBps, UploadKiBps = config.UploadKiBps, PollActiveSeconds = config.PollActiveSeconds, PollIdleMaxSeconds = config.PollIdleMaxSeconds, HydrationWorkers = config.HydrationWorkers, HydrationPolicy = config.HydrationPolicy },
            Active = [.. active.Select(a => new ActiveDto { Id = a.Id, Kind = a.Kind, Name = a.Name, Total = a.Total, Done = a.Done, ElapsedMs = (long)(now - a.StartedUtc).TotalMilliseconds })],
        };
    }

    private SwitchDto BuildSwitch(string? folderParam)
    {
        var co = Infrastructure.Coexistence.Detect(paths.SyncRoot);
        string folder = !string.IsNullOrWhiteSpace(folderParam) ? folderParam! : co.Folder ?? "";
        bool linked = !string.IsNullOrEmpty(folder) && Directory.Exists(folder) && SyncRootRegistrar.IsInsideForeignSyncRoot(folder, out _);
        var a = service.Assistant;
        return new SwitchDto
        {
            Folder = folder, Email = co.Email, OneDriveRunning = co.OneDriveRunning, OneDriveLinked = linked, BackedUp = [.. co.BackedUpFolders],
            IndexReady = service.IndexReady, PendingUploads = service.PendingUploads, PendingOps = service.PendingOps, Adopting = service.Adopting,
            Blockers = a?.Blockers(folder, service.PendingUploads, service.PendingOps, service.IndexReady) ?? ["index-not-ready"],
            Scan = a?.Snapshot() ?? new ScanResult(),
        };
    }

    private static CoexistenceDto ToDto(CoexistenceInfo c) => new() { OneDriveRunning = c.OneDriveRunning, Email = c.Email, Folder = c.Folder, BackedUp = [.. c.BackedUpFolders], Inside = c.NoDriveInsideOneDrive };

    private SaveResultDto SaveSettings(SettingsDto? s)
    {
        if (s is null) { return new SaveResultDto { Error = "empty" }; }
        string lang = s.Language is "en" or "es" ? s.Language : "auto";
        string policy = s.HydrationPolicy == "progressive" ? "progressive" : "full";
        bool restart = policy != config.HydrationPolicy || Math.Clamp(s.HydrationWorkers, 2, 16) != config.HydrationWorkers;
        config.Language = lang;
        config.DownloadKiBps = Math.Clamp(s.DownloadKiBps, 0, 10_000_000);
        config.UploadKiBps = Math.Clamp(s.UploadKiBps, 0, 10_000_000);
        config.PollActiveSeconds = Math.Clamp(s.PollActiveSeconds, 15, 3600);
        config.PollIdleMaxSeconds = Math.Clamp(s.PollIdleMaxSeconds, config.PollActiveSeconds, 86_400);
        config.HydrationWorkers = Math.Clamp(s.HydrationWorkers, 2, 16);
        config.HydrationPolicy = policy;
        config.Save(paths.ConfigPath);
        ActivityLog.ApplyLimits(config);                          // limits and poll intervals apply immediately
        return new SaveResultDto { Ok = true, RestartRequired = restart };
    }

    private string Html()
    {
        if (_html is not null) { return _html; }
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html") ?? throw new InvalidOperationException("index.html is not embedded");
        using var r = new StreamReader(s, Encoding.UTF8);
        return _html = r.ReadToEnd();
    }

    private static async Task<T?> ReadAsync<T>(HttpListenerContext ctx, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await ctx.Request.InputStream.ReadAsync(buf).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > 64 * 1024) { return default; }          // settings are tiny; refuse anything else
        }
        return JsonSerializer.Deserialize(ms.ToArray(), info);
    }

    private static void Json<T>(HttpListenerContext ctx, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.Serialize(value, info));

    private static void Respond(HttpListenerContext ctx, int status, string type, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = type;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.AppendHeader("Cache-Control", "no-store");
        ctx.Response.AppendHeader("X-Content-Type-Options", "nosniff");
        ctx.Response.AppendHeader("Content-Security-Policy", "default-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'");
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static void OpenFolder(string folder)
    {
        ShellOpen.Folder(folder);
    }

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>Windows display language ("en", "es"); the engine runs with invariant globalization, so ask Win32.</summary>
    private static string OsLanguage() => (PInvoke.GetUserDefaultUILanguage() & 0x3FF) switch { 0x0A => "es", _ => "en" };

    /// <summary>Windows regional format as a BCP-47 tag (for example es-AR): drives date, time and number formats in the page.</summary>
    private static unsafe string OsCulture()
    {
        Span<char> buf = stackalloc char[85];
        fixed (char* p = buf)
        {
            int n = PInvoke.GetUserDefaultLocaleName(new Windows.Win32.Foundation.PWSTR(p), buf.Length);
            return n > 1 ? new string(p, 0, n - 1) : "en-US";
        }
    }
}
