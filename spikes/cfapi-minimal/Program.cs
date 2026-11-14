// P0 step 3 spike: unpackaged sync root + placeholders from one delta page + PROGRESSIVE hydration.
//   cfapi-minimal.exe [--root <path>] [--top N] [--win32-register] [--workers N] [--interactive]
//   cfapi-minimal.exe --cleanup [--root <path>]
// Never point --root at your real OneDrive folder: --cleanup unregisters and deletes unhydrated placeholders.

using System.Globalization;
using System.Text.Json.Nodes;
using NoDrive.Graph;
using NoDrive.Spikes.Scale;
using NoDrive.Spikes.CfApiMinimal;
using NoDrive.Spikes.Shared;

string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NoDriveSpike");
int top = 200;
int workers = 3;
bool cleanup = false, forceWin32 = false, interactive = false, probeFlags = false;
string? indexPath = null;
int heapVariant = -1, heapIterations = 3000, heapDelay = 10, heapThreads = 4;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--root": root = Path.GetFullPath(args[++i]); break;
        case "--top": top = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--workers": workers = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--cleanup": cleanup = true; break;
        case "--probe-flags": probeFlags = true; break;
        case "--index": indexPath = args[++i]; break;
        case "--heap-repro": heapVariant = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--iterations": heapIterations = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--delay-ms": heapDelay = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--threads": heapThreads = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--win32-register": forceWin32 = true; break;
        case "--interactive": interactive = true; break;
    }
}

Console.WriteLine($"cfapi-minimal root={root} aot={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported} pid={Environment.ProcessId}");
ProcessStats.PrintGcConfiguration();

if (heapVariant >= 0)
{
    string heapRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "NoDriveHeapRepro");
    int code = HeapRepro.Run(heapRoot, heapVariant, heapIterations, heapDelay, heapThreads);
    HeapRepro.Unregister(heapRoot);
    try { Directory.Delete(heapRoot, true); } catch { }
    return code;
}

if (probeFlags)
{
    await SyncRootRegistrar.ProbeFlagsAsync(Path.Combine(Path.GetTempPath(), "NoDriveProbe"));
    return 0;
}

if (cleanup)
{
    CloudProvider.Disconnect();
    SyncRootRegistrar.Unregister(root);
    Console.WriteLine("cleanup done. Remaining files in the root are the hydrated ones; delete the folder by hand if you want.");
    return 0;
}

// Safety: refuse to register inside another provider's root (e.g. the real OneDrive folder).
bool ours = SyncRootRegistrar.IsRegisteredByUs();
if (Directory.Exists(root) && SyncRootRegistrar.IsUnderSyncRoot(root, out string owner))
{
    if (ours || owner.Contains("NoDrive", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"root is already registered (provider '{owner}', previous run); reusing the registration and existing placeholders");
    }
    else
    {
        Console.WriteLine($"STOP: {root} is inside a sync root owned by '{owner}'. Choose another --root.");
        return 3;
    }
}
if (!ours && Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !SyncRootRegistrar.IsUnderSyncRoot(root, out _))
{
    Console.WriteLine($"STOP: {root} exists and is not empty. Use an empty folder for the spike.");
    return 3;
}

// ---- Index mode (P0 step 6, cfapi half): PARTIAL population, FETCH_PLACEHOLDERS answered from the SQLite index. No Graph, no placeholders pre-created. ----
if (indexPath is not null)
{
    ItemStore.InitProvider();
    var store = new ItemStore(indexPath);
    Console.WriteLine($"index mode: {indexPath} with {store.Count()} items; population policy FULL (on demand, via FETCH_PLACEHOLDERS)");
    bool ours2 = SyncRootRegistrar.IsRegisteredByUs();
    if (!ours2)
    {
        Directory.CreateDirectory(root);
        string how2 = await SyncRootRegistrar.RegisterAsync(root, forceWin32, partialPopulation: true);
        Console.WriteLine($"registered via {how2}");
        Console.WriteLine($"shell flags -> {SyncRootRegistrar.ShellIntegrationFlags}: {(SyncRootRegistrar.TryApplyShellFlags(out string why2) ? "applied" : "NOT applied (" + why2 + ")")}");
    }
    else { Console.WriteLine("existing registration kept (run --cleanup first if it was registered with another population policy)"); }
    using var noGraph = GraphLite.NewClient();
    CloudProvider.Connect(root, noGraph, 2);
    CloudProvider.UseIndex(store);
    SyncRootRegistrar.PrintEffectivePolicies(root);
    PinWatcher.Start(root);
    _ = Task.Run(async () => { while (true) { await Task.Delay(TimeSpan.FromSeconds(10)); var l = CloudProvider.CallbackMicros.OrderBy(x => x).ToArray(); if (l.Length > 0) { Console.WriteLine($"[latency] callbacks n={l.Length} p50={l[l.Length / 2]:F0} us p99={l[(int)(l.Length * 0.99)]:F0} us max={l[^1]:F0} us"); } } });
    string? prefetchIds = Environment.GetEnvironmentVariable("NODRIVE_PREFETCH");   // comma-separated folder ids to populate in the background
    if (!string.IsNullOrWhiteSpace(prefetchIds)) { Prefetcher.Start(store, root, "F61C08DC8B03ABFF!101", prefetchIds.Split(',', StringSplitOptions.RemoveEmptyEntries), 20); }
    Console.WriteLine(ProcessStats.Sample("ready"));
    Console.WriteLine("Open the folders in Explorer; every FETCH_PLACEHOLDERS is logged as [populate]. Hydration of these synthetic items is expected to fail (their ids do not exist in OneDrive).");
    TrayWindow.Create("NoDriveCfApiIndex", "NoDrive cfapi-minimal (index)", withIcon: true, onQuit: CloudProvider.Disconnect);
    ProcessStats.StartSampler(TimeSpan.FromSeconds(30));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; TrayWindow.RequestQuit(); };
    TrayWindow.RunMessageLoop();
    PinWatcher.Stop();
    CloudProvider.Disconnect();
    TrayWindow.Destroy();
    store.Dispose();
    return 0;
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
// ADR-0009: Register() resets SyncRootManager Flags to 34, so an existing registration is NOT re-registered; the engine only connects.
if (ours)
{
    Console.WriteLine($"existing registration kept (not re-registered): id={SyncRootRegistrar.SyncRootId}");
}
else
{
    string how = await SyncRootRegistrar.RegisterAsync(root, forceWin32);
    Console.WriteLine($"registered via {how}: id={SyncRootRegistrar.SyncRootId}");
    bool applied = SyncRootRegistrar.TryApplyShellFlags(out string why);
    Console.WriteLine($"shell flags -> {SyncRootRegistrar.ShellIntegrationFlags}: {(applied ? "applied" : "NOT applied (" + why + ")")}");
}
int? flags = SyncRootRegistrar.ReadShellFlags();
Console.WriteLine($"SyncRootManager Flags = {flags} ({(flags == SyncRootRegistrar.ShellIntegrationFlags ? "native Explorer integration" : "run elevated setup: verbs/Status/icons will be limited")})");
CloudProvider.Connect(root, graph, workers);
PinWatcher.Start(root);
SyncRootRegistrar.PrintEffectivePolicies(root);

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
    var children = group.Select(x => x.Item)
        .Where(i => !Path.Exists(Path.Combine(dir, i.Name)))   // re-run: keep placeholders from the previous run
        .OrderByDescending(i => i.IsFolder).ToList();
    created += PlaceholderWriter.CreateChildren(dir, children);
}
Console.WriteLine($"placeholders created: {created}");
Console.WriteLine(ProcessStats.Sample("ready"));
Console.WriteLine();
Console.WriteLine("Open the folder in Explorer, double-click an online-only file, pin a folder, rename/delete something.");
Console.WriteLine("Quit from the tray menu (disconnects, keeps the registration). Run with --cleanup to unregister.");
Console.WriteLine("This process now BLOCKS and waits for Explorer activity; every callback and hydration is logged below.");
Console.WriteLine();
_ = Task.Run(async () =>
{
    await Task.Delay(TimeSpan.FromMinutes(2));
    if (CloudProvider.CallbackMicros.Count == 0)
    {
        Console.WriteLine("(no callbacks after 2 minutes: open Explorer at the folder above and double-click a file to trigger a hydration)");
    }
});

TrayWindow.Create("NoDriveCfApiMinimal", "NoDrive cfapi-minimal", withIcon: true, onQuit: CloudProvider.Disconnect);
ProcessStats.StartSampler(TimeSpan.FromSeconds(30));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; TrayWindow.RequestQuit(); };
TrayWindow.RunMessageLoop();
PinWatcher.Stop();
CloudProvider.Disconnect();
TrayWindow.Destroy();

var lat = CloudProvider.CallbackMicros.OrderBy(x => x).ToArray();
if (lat.Length > 0)
{
    Console.WriteLine($"callback latency: n={lat.Length} p50={lat[lat.Length / 2]:F0} us p99={lat[(int)(lat.Length * 0.99)]:F0} us max={lat[^1]:F0} us");
}
Console.WriteLine(ProcessStats.Sample("exit"));
return 0;
