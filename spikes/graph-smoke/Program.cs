// P0 step 1 spike: prove that the app registration + PKCE + Graph delta work against a personal
// OneDrive, and record the facts the plan needs: 403 serviceReadOnly or not, items per delta page
// with $top, Range support on @microsoft.graph.downloadUrl. Throwaway code; see spikes/README.md.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NoDrive.Graph;

int maxPages = 3;
int top = 1000;
bool forceInteractive = false;
int largest = 0;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pages": maxPages = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--top": top = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--interactive": forceInteractive = true; break;
        case "--all": maxPages = int.MaxValue; break;
        case "--largest": largest = int.Parse(args[++i], CultureInfo.InvariantCulture); maxPages = int.MaxValue; break;
    }
}

string tokenDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nodrive-spike");
string tokenPath = Path.Combine(tokenDir, "token.json");
string clientId = Environment.GetEnvironmentVariable("NODRIVE_CLIENT_ID") ?? GraphEndpoints.DefaultClientId;
string scope = string.Join(' ', GraphEndpoints.Scopes);

using var http = new HttpClient(new SocketsHttpHandler
{
    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
});
http.Timeout = TimeSpan.FromSeconds(100);
http.DefaultRequestHeaders.UserAgent.ParseAdd("ISV|NoDrive|NoDrive/0.0.1-spike");

Console.WriteLine($"client id : {clientId}");
Console.WriteLine($"authority : {GraphEndpoints.Authority}");
Console.WriteLine($"scopes    : {scope}");
Console.WriteLine($"token file: {tokenPath}");
Console.WriteLine();

// ---------- 1. Token ----------
JsonNode token;
if (!forceInteractive && File.Exists(tokenPath))
{
    var saved = JsonNode.Parse(await File.ReadAllTextAsync(tokenPath))!;
    Console.WriteLine("Refreshing with saved refresh_token ...");
    var sw = Stopwatch.StartNew();
    var form = new Dictionary<string, string>
    {
        ["client_id"] = clientId,
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = saved["refresh_token"]!.GetValue<string>(),
        ["scope"] = scope,
    };
    using var resp = await http.PostAsync($"{GraphEndpoints.Authority}/token", new FormUrlEncodedContent(form));
    var body = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode)
    {
        Console.WriteLine($"  refresh failed {(int)resp.StatusCode}: {body}");
        Console.WriteLine("  falling back to interactive sign-in");
        token = await InteractiveSignInAsync();
    }
    else
    {
        token = JsonNode.Parse(body)!;
        Console.WriteLine($"  ok in {sw.ElapsedMilliseconds} ms, expires_in={token["expires_in"]}, refresh_token rotated={(token["refresh_token"] is not null)}");
    }
}
else
{
    token = await InteractiveSignInAsync();
}

Directory.CreateDirectory(tokenDir);
await File.WriteAllTextAsync(tokenPath, token.ToJsonString());
if (!OperatingSystem.IsWindows())
{
    File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
}

string accessToken = token["access_token"]!.GetValue<string>();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
Console.WriteLine($"granted scope: {token["scope"]}");
Console.WriteLine();

// ---------- 2. /me/drive ----------
{
    var (status, body, ms) = await GetAsync($"{GraphEndpoints.BaseUrl}/me/drive?$select=id,driveType,owner,quota");
    Console.WriteLine($"GET /me/drive -> {status} in {ms} ms");
    if (status != HttpStatusCode.OK)
    {
        Console.WriteLine(body);
        Console.WriteLine("STOP: drive not accessible (this is the 403 serviceReadOnly / 503 provisioning check).");
        return 2;
    }
    var drive = JsonNode.Parse(body)!;
    var quota = drive["quota"];
    Console.WriteLine($"  drive id={drive["id"]} type={drive["driveType"]} owner={drive["owner"]?["user"]?["displayName"]}");
    Console.WriteLine($"  quota total={Gb(quota?["total"])} used={Gb(quota?["used"])} remaining={Gb(quota?["remaining"])} deleted={Gb(quota?["deleted"])}");
    Console.WriteLine();
}

// ---------- 3. Delta pages ----------
string? firstFileId = null;
long firstFileSize = 0;
string? firstFileName = null;
var biggest = new List<(long Size, string Id, string Name, string Parent)>();
{
    string? url = $"{GraphEndpoints.BaseUrl}{GraphEndpoints.Delta}?$select=id,name,size,lastModifiedDateTime,folder,file,parentReference,deleted,cTag,eTag&$top={top}";
    int page = 0, items = 0, folders = 0, files = 0, deleted = 0;
    var total = Stopwatch.StartNew();
    while (url is not null && page < maxPages)
    {
        var (status, body, ms) = await GetAsync(url);
        page++;
        if (status != HttpStatusCode.OK)
        {
            Console.WriteLine($"delta page {page} -> {status} in {ms} ms");
            Console.WriteLine(body);
            break;
        }
        var node = JsonNode.Parse(body)!;
        var value = node["value"]!.AsArray();
        int pf = 0, pd = 0, pdel = 0;
        foreach (var it in value)
        {
            if (it!["deleted"] is not null) { pdel++; continue; }
            if (it["folder"] is not null) { pd++; }
            else if (it["file"] is not null)
            {
                pf++;
                long size = it["size"]?.GetValue<long>() ?? 0;
                if (largest > 0)
                {
                    biggest.Add((size, it["id"]!.GetValue<string>(), it["name"]!.GetValue<string>(), it["parentReference"]?["path"]?.GetValue<string>() ?? "?"));
                    if (biggest.Count > largest * 4) { biggest.Sort((a, b) => b.Size.CompareTo(a.Size)); biggest.RemoveRange(largest, biggest.Count - largest); }
                }
                if (firstFileId is null && size > 4096)
                {
                    firstFileId = it["id"]!.GetValue<string>();
                    firstFileSize = size;
                    firstFileName = it["name"]!.GetValue<string>();
                }
            }
        }
        items += value.Count; folders += pd; files += pf; deleted += pdel;
        string? next = node["@odata.nextLink"]?.GetValue<string>();
        string? delta = node["@odata.deltaLink"]?.GetValue<string>();
        Console.WriteLine($"delta page {page,4}: {value.Count,5} items ({pd} folders, {pf} files, {pdel} deleted) in {ms,5} ms, body {body.Length / 1024,5} KiB{(delta is not null ? "  [deltaLink reached]" : "")}");
        url = next;
    }
    Console.WriteLine($"  total {items} items ({folders} folders, {files} files, {deleted} deleted) over {page} page(s) in {total.ElapsedMilliseconds} ms; $top requested {top}");
    if (url is not null && page >= maxPages) { Console.WriteLine($"  stopped at --pages {maxPages}; more pages remain (use --all to enumerate everything)"); }
    Console.WriteLine();
    if (largest > 0)
    {
        biggest.Sort((a, b) => b.Size.CompareTo(a.Size));
        Console.WriteLine($"largest {Math.Min(largest, biggest.Count)} files (use the id with: aot-baseline.exe --variant http --item <id>):");
        foreach (var f in biggest.Take(largest))
        {
            Console.WriteLine($"  {f.Size / 1048576.0,9:F1} MB  {f.Id}  {f.Name}   [{f.Parent}]");
        }
        Console.WriteLine();
    }
}

// ---------- 4. Range download on downloadUrl ----------
if (firstFileId is not null)
{
    // Attempt A: full item GET (no $select) should carry @microsoft.graph.downloadUrl.
    var (status, body, ms) = await GetAsync($"{GraphEndpoints.BaseUrl}/me/drive/items/{firstFileId}");
    Console.WriteLine($"GET item '{firstFileName}' ({firstFileSize} bytes) without $select -> {status} in {ms} ms");
    string? dl = null;
    if (status == HttpStatusCode.OK)
    {
        var item = JsonNode.Parse(body)!;
        dl = item["@microsoft.graph.downloadUrl"]?.GetValue<string>();
        Console.WriteLine($"  @microsoft.graph.downloadUrl present: {dl is not null}; cTag={item["cTag"]}; hashes={item["file"]?["hashes"]?.ToJsonString()}");
    }
    else { Console.WriteLine(body); }

    // Attempt B: select with the documented spelling (no $ prefix).
    if (dl is null)
    {
        var (s2, b2, ms2) = await GetAsync($"{GraphEndpoints.BaseUrl}/me/drive/items/{firstFileId}?select=id,@microsoft.graph.downloadUrl");
        Console.WriteLine($"GET item ?select=id,@microsoft.graph.downloadUrl -> {s2} in {ms2} ms");
        if (s2 == HttpStatusCode.OK) { dl = JsonNode.Parse(b2)!["@microsoft.graph.downloadUrl"]?.GetValue<string>(); Console.WriteLine($"  downloadUrl present: {dl is not null}"); }
    }

    // Attempt C: /content 302 redirect, captured without following.
    if (dl is null)
    {
        using var noRedirect = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        noRedirect.DefaultRequestHeaders.Authorization = http.DefaultRequestHeaders.Authorization;
        noRedirect.DefaultRequestHeaders.UserAgent.ParseAdd("ISV|NoDrive|NoDrive/0.0.1-spike");
        var sw = Stopwatch.StartNew();
        using var resp = await noRedirect.GetAsync($"{GraphEndpoints.BaseUrl}/me/drive/items/{firstFileId}/content", HttpCompletionOption.ResponseHeadersRead);
        Console.WriteLine($"GET /content (no follow) -> {(int)resp.StatusCode} {resp.StatusCode} in {sw.ElapsedMilliseconds} ms, Location present: {resp.Headers.Location is not null}");
        dl = resp.Headers.Location?.ToString();
    }

    if (dl is not null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, dl);
        req.Headers.Range = new RangeHeaderValue(0, 1023);
        using var plain = new HttpClient(new SocketsHttpHandler()); // pre-authenticated URL: no Authorization header
        plain.DefaultRequestHeaders.UserAgent.ParseAdd("ISV|NoDrive|NoDrive/0.0.1-spike");
        var sw = Stopwatch.StartNew();
        using var resp = await plain.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Console.WriteLine($"  Range 0-1023 -> {(int)resp.StatusCode} {resp.StatusCode}, got {bytes.Length} bytes, Content-Range={resp.Content.Headers.ContentRange}, Accept-Ranges={string.Join(',', resp.Headers.AcceptRanges)} in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"  downloadUrl host={new Uri(dl).Host}");

        // Second range in the middle of the file, to prove random access (PROGRESSIVE hydration needs it).
        using var req2 = new HttpRequestMessage(HttpMethod.Get, dl);
        long mid = firstFileSize / 2;
        req2.Headers.Range = new RangeHeaderValue(mid, mid + 4095);
        sw.Restart();
        using var resp2 = await plain.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead);
        var bytes2 = await resp2.Content.ReadAsByteArrayAsync();
        Console.WriteLine($"  Range {mid}-{mid + 4095} -> {(int)resp2.StatusCode} {resp2.StatusCode}, got {bytes2.Length} bytes, Content-Range={resp2.Content.Headers.ContentRange} in {sw.ElapsedMilliseconds} ms");
    }
    Console.WriteLine();
}
else
{
    Console.WriteLine("No file > 4 KiB seen in the enumerated pages; skipping the Range test.");
}

Console.WriteLine($"process: working set {Environment.WorkingSet / 1024 / 1024} MB, GC heap {GC.GetTotalMemory(false) / 1024 / 1024} MB");
return 0;

// ---------- helpers ----------
async Task<(HttpStatusCode status, string body, long ms)> GetAsync(string url)
{
    var sw = Stopwatch.StartNew();
    using var resp = await http.GetAsync(url);
    var body = await resp.Content.ReadAsStringAsync();
    if (resp.StatusCode == HttpStatusCode.TooManyRequests || resp.StatusCode == HttpStatusCode.ServiceUnavailable)
    {
        Console.WriteLine($"  throttled: {(int)resp.StatusCode}, Retry-After={resp.Headers.RetryAfter}");
    }
    return (resp.StatusCode, body, sw.ElapsedMilliseconds);
}

async Task<JsonNode> InteractiveSignInAsync()
{
    string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
    string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    string state = Base64Url(RandomNumberGenerator.GetBytes(16));

    int port = FreePort();
    string redirect = $"http://localhost:{port}";
    using var listener = new HttpListener();
    listener.Prefixes.Add(redirect + "/");
    listener.Start();

    string authorize = $"{GraphEndpoints.Authority}/authorize?client_id={Uri.EscapeDataString(clientId)}" +
        $"&response_type=code&redirect_uri={Uri.EscapeDataString(redirect)}&response_mode=query" +
        $"&scope={Uri.EscapeDataString(scope)}&code_challenge={challenge}&code_challenge_method=S256&state={state}&prompt=select_account";

    Console.WriteLine("Opening the browser for sign-in. If it does not open, paste this URL:");
    Console.WriteLine(authorize);
    Console.WriteLine();
    try { Process.Start(new ProcessStartInfo(authorize) { UseShellExecute = true }); }
    catch (Exception ex) { Console.WriteLine($"(could not launch browser: {ex.Message})"); }

    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    HttpListenerContext ctx;
    try { ctx = await listener.GetContextAsync().WaitAsync(cts.Token); }
    catch (OperationCanceledException) { throw new TimeoutException("No redirect received within 5 minutes."); }

    var q = ctx.Request.QueryString;
    string html;
    if (q["error"] is not null)
    {
        html = $"<h2>NoDrive spike: sign-in failed</h2><p>{WebUtility.HtmlEncode(q["error"])}: {WebUtility.HtmlEncode(q["error_description"])}</p>";
    }
    else if (q["state"] != state || q["code"] is null)
    {
        html = "<h2>NoDrive spike: invalid response (state mismatch or no code)</h2>";
    }
    else
    {
        html = "<h2>NoDrive spike: signed in. You can close this window.</h2>";
    }
    var buf = Encoding.UTF8.GetBytes($"<!doctype html><html><body style='font-family:sans-serif'>{html}</body></html>");
    ctx.Response.ContentType = "text/html; charset=utf-8";
    ctx.Response.ContentLength64 = buf.Length;
    await ctx.Response.OutputStream.WriteAsync(buf);
    ctx.Response.Close();
    listener.Stop();

    if (q["error"] is not null) { throw new InvalidOperationException($"{q["error"]}: {q["error_description"]}"); }
    if (q["state"] != state || q["code"] is null) { throw new InvalidOperationException("state mismatch or missing code"); }

    var sw = Stopwatch.StartNew();
    var form = new Dictionary<string, string>
    {
        ["client_id"] = clientId,
        ["grant_type"] = "authorization_code",
        ["code"] = q["code"]!,
        ["redirect_uri"] = redirect,
        ["code_verifier"] = verifier,
        ["scope"] = scope,
    };
    using var resp = await http.PostAsync($"{GraphEndpoints.Authority}/token", new FormUrlEncodedContent(form));
    var body = await resp.Content.ReadAsStringAsync();
    if (!resp.IsSuccessStatusCode) { throw new InvalidOperationException($"token endpoint {(int)resp.StatusCode}: {body}"); }
    Console.WriteLine($"token exchange ok in {sw.ElapsedMilliseconds} ms");
    return JsonNode.Parse(body)!;
}

static int FreePort()
{
    var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    l.Start();
    int port = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return port;
}

static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static string Gb(JsonNode? n) => n is null ? "?" : (n.GetValue<long>() / 1024.0 / 1024 / 1024).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
