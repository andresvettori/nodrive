// PKCE + system browser + loopback listener, with a plain-JSON token file (spike only; the engine will
// use DPAPI). Also small Graph helpers the spikes share. Uses JsonNode (AOT-safe, no reflection).

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NoDrive.Graph;

namespace NoDrive.Spikes.Shared;

internal sealed class SpikeAuth
{
    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _scope = string.Join(' ', GraphEndpoints.Scopes);
    private readonly string _tokenPath;

    public SpikeAuth(HttpClient http, string tokenPath, string? clientId = null)
    {
        _http = http;
        _tokenPath = tokenPath;
        _clientId = clientId ?? Environment.GetEnvironmentVariable("NODRIVE_CLIENT_ID") ?? GraphEndpoints.DefaultClientId;
    }

    public static string DefaultTokenPath()
    {
        string baseDir = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoDrive", "spike")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nodrive-spike");
        return Path.Combine(baseDir, "token.json");
    }

    /// <summary>Returns a valid access token, refreshing or signing in interactively as needed, and persists the rotated refresh token.</summary>
    public async Task<string> GetAccessTokenAsync(bool forceInteractive = false)
    {
        JsonNode token;
        if (!forceInteractive && File.Exists(_tokenPath))
        {
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(_tokenPath))!;
            var form = new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = saved["refresh_token"]!.GetValue<string>(),
                ["scope"] = _scope,
            };
            using var resp = await _http.PostAsync($"{GraphEndpoints.Authority}/token", new FormUrlEncodedContent(form));
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode)
            {
                token = JsonNode.Parse(body)!;
                Console.WriteLine("auth: refresh ok");
            }
            else
            {
                Console.WriteLine($"auth: refresh failed {(int)resp.StatusCode}, signing in interactively");
                token = await InteractiveAsync();
            }
        }
        else
        {
            token = await InteractiveAsync();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
        await File.WriteAllTextAsync(_tokenPath, token.ToJsonString());
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(_tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        return token["access_token"]!.GetValue<string>();
    }

    private async Task<JsonNode> InteractiveAsync()
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));
        int port = FreePort();
        string redirect = $"http://localhost:{port}";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect + "/");
        listener.Start();

        string authorize = $"{GraphEndpoints.Authority}/authorize?client_id={Uri.EscapeDataString(_clientId)}" +
            $"&response_type=code&redirect_uri={Uri.EscapeDataString(redirect)}&response_mode=query" +
            $"&scope={Uri.EscapeDataString(_scope)}&code_challenge={challenge}&code_challenge_method=S256&state={state}&prompt=select_account";
        Console.WriteLine("auth: opening browser; if nothing opens, paste this URL:");
        Console.WriteLine(authorize);
        try { Process.Start(new ProcessStartInfo(authorize) { UseShellExecute = true }); } catch (Exception ex) { Console.WriteLine($"(browser launch failed: {ex.Message})"); }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ctx = await listener.GetContextAsync().WaitAsync(cts.Token);
        var q = ctx.Request.QueryString;
        bool ok = q["error"] is null && q["state"] == state && q["code"] is not null;
        var buf = Encoding.UTF8.GetBytes(ok
            ? "<!doctype html><body style='font-family:sans-serif'><h2>NoDrive spike: signed in. You can close this window.</h2></body>"
            : $"<!doctype html><body style='font-family:sans-serif'><h2>NoDrive spike: sign-in failed</h2><p>{WebUtility.HtmlEncode(q["error"])}: {WebUtility.HtmlEncode(q["error_description"])}</p></body>");
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = buf.Length;
        await ctx.Response.OutputStream.WriteAsync(buf);
        ctx.Response.Close();
        listener.Stop();
        if (!ok) { throw new InvalidOperationException($"sign-in failed: {q["error"]} {q["error_description"]}"); }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = q["code"]!,
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
            ["scope"] = _scope,
        };
        using var resp = await _http.PostAsync($"{GraphEndpoints.Authority}/token", new FormUrlEncodedContent(form));
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) { throw new InvalidOperationException($"token endpoint {(int)resp.StatusCode}: {body}"); }
        Console.WriteLine("auth: interactive sign-in ok");
        return JsonNode.Parse(body)!;
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal static class GraphLite
{
    public static HttpClient NewClient(string? bearer = null)
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
        })
        { Timeout = TimeSpan.FromSeconds(100) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ISV|NoDrive|NoDrive/0.0.1-spike");
        if (bearer is not null) { http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer); }
        return http;
    }

    public static async Task<JsonNode> GetJsonAsync(HttpClient http, string url, CancellationToken ct = default)
    {
        using var resp = await http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{(int)resp.StatusCode} {url}: {body}", null, resp.StatusCode);
        }
        return JsonNode.Parse(body)!;
    }

    /// <summary>Plain item GET (no $select): the only form that reliably carries @microsoft.graph.downloadUrl on personal accounts (measured 2026-11-13).</summary>
    public static async Task<string> GetDownloadUrlAsync(HttpClient http, string itemId, CancellationToken ct = default)
    {
        var item = await GetJsonAsync(http, $"{GraphEndpoints.BaseUrl}/me/drive/items/{itemId}", ct);
        return item["@microsoft.graph.downloadUrl"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"item {itemId} has no downloadUrl");
    }
}
