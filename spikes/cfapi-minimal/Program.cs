// P0 step 3 spike: unpackaged sync root + placeholders from one delta page + PROGRESSIVE hydration.
//   cfapi-minimal.exe [--root <path>] [--top N] [--win32-register] [--workers N] [--interactive]
//   cfapi-minimal.exe --cleanup [--root <path>]
// Never point --root at your real OneDrive folder: --cleanup unregisters and deletes unhydrated placeholders.

using System.Globalization;
using System.Text.Json.Nodes;
using NoDrive.Graph;
using NoDrive.Spikes.CfApiMinimal;
using NoDrive.Spikes.Shared;

string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NoDriveSpike");
int top = 200;
int workers = 3;
bool cleanup = false, forceWin32 = false, interactive = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--root": root = Path.GetFullPath(args[++i]); break;
        case "--top": top = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--workers": workers = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--cleanup": cleanup = true; break;
        case "--win32-register": forceWin32 = true; break;
        case "--interactive": interactive = true; break;
    }
}

Console.WriteLine($"cfapi-minimal root={root} aot={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported} pid={Environment.ProcessId}");
ProcessStats.PrintGcConfiguration();

if (cleanup)
{
    CloudProvider.Disconnect();
    SyncRootRegistrar.Unregister(root);
    Console.WriteLine("cleanup done. Remaining files in the root are the hydrated ones; delete the folder by hand if you want.");
    return 0;
}

// Safety: refuse to register inside another provider's root (e.g. the real OneDrive folder).
if (Directory.Exists(root) && SyncRootRegistrar.IsUnderSyncRoot(root, out string owner))
{
    if (owner.Contains("NoDrive", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"root is already registered by '{owner}' (previous run); reusing the registration");
    }
    else
    {
        Console.WriteLine($"STOP: {root} is inside a sync root owned by '{owner}'. Choose another --root.");
        return 3;
    }
}
if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !SyncRootRegistrar.IsUnderSyncRoot(root, out _))
{
    Console.WriteLine($"STOP: {root} exists and is not empty. Use an empty folder for the spike.");
    return 3;
}

// 1. Sign in and fetch one delta page.
using var anon = GraphLite.NewClient();
var auth = new SpikeAuth(anon, SpikeAuth.DefaultTokenPath());
string accessToken = await auth.GetAccessTokenAsync(interactive);
using var graph = GraphLite.NewClient(accessToken);

var rootItem = await GraphLite.GetJsonAsync(graph, $"{GraphEndpoints.BaseUrl}{GraphEndpoints.DriveRoot}?$select=id");
string rootId = rootItem["id"]!.GetValue<string>();
var page = await GraphLite.GetJsonAsync(graph, $"{GraphEndpoints.BaseUrl}{GraphEndpoints.Delta}?$select=id,name,size,lastModifiedDateTime,createdDateTime,folder,file,parentReference,deleted&$top={top}");

var items = new Dictionary<string, RemoteItem>(StringComparer.Ordinal);
foreach (var n in page["value"]!.AsArray())
{
    if (n!["deleted"] is not null) { continue; }
    string id = n["id"]!.GetValue<string>();
    if (id == rootId) { continue; }
    items[id] = new RemoteItem(
        id,
        n["name"]!.GetValue<string>(),
        n["parentReference"]?["id"]?.GetValue<string>(),
        n["folder"] is not null,
        n["size"]?.GetValue<long>() ?? 0,
        DateTimeOffset.Parse(n["lastModifiedDateTime"]!.GetValue<string>(), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(n["createdDateTime"]?.GetValue<string>() ?? n["lastModifiedDateTime"]!.GetValue<string>(), CultureInfo.InvariantCulture));
}
Console.WriteLine($"delta page: {items.Count} items ({items.Values.Count(i => i.IsFolder)} folders)");

// Resolve relative paths for items whose parent chain is inside this page (or the root).
var relPath = new Dictionary<string, string>(StringComparer.Ordinal);
string? Resolve(string id)
{
    if (relPath.TryGetValue(id, out var known)) { return known; }
    if (!items.TryGetValue(id, out var it)) { return null; }
    string? parent = it.ParentId == rootId ? "" : it.ParentId is null ? null : Resolve(it.ParentId);
    if (parent is null) { return null; }
    string p = parent.Length == 0 ? it.Name : Path.Combine(parent, it.Name);
    relPath[id] = p;
    return p;
}
int unresolved = 0;
foreach (var id in items.Keys) { if (Resolve(id) is null) { unresolved++; } }
Console.WriteLine($"resolvable under root: {relPath.Count}, skipped (parent not in this page): {unresolved}");

// 2. Register and connect.
string how = await SyncRootRegistrar.RegisterAsync(root, forceWin32);
Console.WriteLine($"registered via {how}: id={SyncRootRegistrar.SyncRootId}");
CloudProvider.Connect(root, graph, workers);

// 3. Create placeholders, directories first, shallow to deep.
var byParentDir = relPath
    .Select(kv => (Item: items[kv.Key], Rel: kv.Value))
    .GroupBy(x => Path.GetDirectoryName(x.Rel) ?? "")
    .OrderBy(g => g.Key.Count(c => c == Path.DirectorySeparatorChar))
    .ThenBy(g => g.Key, StringComparer.Ordinal);
int created = 0;
foreach (var group in byParentDir)
{
    string dir = Path.Combine(root, group.Key);
    if (!Directory.Exists(dir)) { Console.WriteLine($"  skipping {group.Key}: parent placeholder missing"); continue; }
    var children = group.Select(x => x.Item).OrderByDescending(i => i.IsFolder).ToList();
    created += PlaceholderWriter.CreateChildren(dir, children);
}
Console.WriteLine($"placeholders created: {created}");
Console.WriteLine(ProcessStats.Sample("ready"));
Console.WriteLine();
Console.WriteLine("Open the folder in Explorer, double-click an online-only file, pin a folder, rename/delete something.");
Console.WriteLine("Quit from the tray menu (disconnects, keeps the registration). Run with --cleanup to unregister.");

TrayWindow.Create("NoDriveCfApiMinimal", "NoDrive cfapi-minimal", withIcon: true, onQuit: CloudProvider.Disconnect);
ProcessStats.StartSampler(TimeSpan.FromSeconds(30));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; TrayWindow.RequestQuit(); };
TrayWindow.RunMessageLoop();
CloudProvider.Disconnect();
TrayWindow.Destroy();

var lat = CloudProvider.CallbackMicros.OrderBy(x => x).ToArray();
if (lat.Length > 0)
{
    Console.WriteLine($"callback latency: n={lat.Length} p50={lat[lat.Length / 2]:F0} us p99={lat[(int)(lat.Length * 0.99)]:F0} us max={lat[^1]:F0} us");
}
Console.WriteLine(ProcessStats.Sample("exit"));
return 0;
