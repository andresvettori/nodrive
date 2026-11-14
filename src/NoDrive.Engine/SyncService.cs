using System.Diagnostics;
using NoDrive.Core.Hydration;
using NoDrive.Core.Index;
using NoDrive.Core.Sync;
using NoDrive.Engine.CloudFiles;
using NoDrive.Engine.Infrastructure;
using NoDrive.Engine.Security;
using NoDrive.Graph;
using NoDrive.Graph.Auth;

namespace NoDrive.Engine;

/// <summary>
/// Orchestrates one engine run: sign in, register the sync root (once), connect the provider, ingest the drive into the index (resumable),
/// then keep polling for changes and prefetch heavy folders. The first sync of a large drive takes minutes (network bound, about 3 s per
/// 1,000 items); until it finishes, folders opened in Explorer fail fast with "provider unavailable" instead of hanging.
/// Milestone 1 scope: remote changes after the first sync update the index but are NOT yet pushed into folders already populated on disk.
/// </summary>
internal sealed class SyncService(EngineConfig config, EnginePaths paths) : IDisposable
{
    private readonly CallerStats _callers = new();
    private ItemIndex? _index;
    private HttpClient? _http;
    private PkceAuthenticator? _auth;
    private volatile bool _indexReady;
    private volatile string _status = "Starting";

    public string Status => _status;
    public CallerStats Callers => _callers;

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(paths.DataDir);
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());              // the system SQLite (System32\winsqlite3.dll)
        _index = new ItemIndex(paths.IndexPath);
        _http = new HttpClient();
        _auth = new PkceAuthenticator(_http, new DpapiTokenStore(paths.TokenPath), OpenBrowser, config.ClientId);
        var graph = new GraphDrive(_http, tokenCt => _auth.GetAccessTokenAsync(tokenCt, allowInteractive: true), new ThrottleGate(), new GraphOptions());

        _status = "Signing in";
        await _auth.GetAccessTokenAsync(ct, allowInteractive: true).ConfigureAwait(false);

        string rootId = _index.GetMeta("root.id") ?? await graph.GetRootIdAsync(ct).ConfigureAwait(false);
        _index.SetMeta("root.id", rootId);

        if (SyncRootRegistrar.IsInsideForeignSyncRoot(paths.SyncRoot, out string foreign)) { throw new InvalidOperationException($"{paths.SyncRoot} is inside the sync root of '{foreign}'. Choose another folder (config.json: syncRoot)."); }
        var (fresh, outcome) = await SyncRootRegistrar.EnsureRegisteredAsync(paths.SyncRoot).ConfigureAwait(false);
        Log.Info($"sync root {paths.SyncRoot}: {outcome}; shell flags {SyncRootRegistrar.ReadFlags()}");

        var population = new PopulationState(_index, rootId);
        if (fresh || !Directory.Exists(paths.SyncRoot)) { population.ResetAll(); }          // a new registration has no placeholders yet
        var locks = new FolderLocks();
        var policy = new HydrationPolicy();
        var hydrator = new Hydrator(graph, policy, new FailureMemory(policy.FailureMemory));

        _indexReady = _index.GetMeta(DeltaIngestor.DeltaKey) is not null && _index.GetMeta(DeltaIngestor.NextKey) is null;
        CloudProvider.Start(paths.SyncRoot, new ProviderServices(_index, hydrator, population, locks, _callers, () => _indexReady, config.HydrationWorkers));
        PinWatcher.Start(paths.SyncRoot);
        _ = Task.Run(() => ReportLoopAsync(ct), CancellationToken.None);

        var prefetcher = new Prefetcher(_index, population, locks, paths.SyncRoot, config.ToPopulationOptions());
        var ingestor = new DeltaIngestor(graph, _index) { OnProgress = (pages, items) => _status = $"Indexing OneDrive: {items:N0} items" };
        int idleSeconds = config.PollActiveSeconds;
        bool prefetchStarted = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var result = await ingestor.RunAsync(ct).ConfigureAwait(false);
                bool first = !_indexReady;
                _indexReady = true;
                long changes = result.Upserts + result.Deletions;
                if (first || changes > 0) { Log.Info($"delta: {result.Pages} pages, {result.Upserts:N0} upserts, {result.Deletions:N0} deletions in {sw.Elapsed.TotalSeconds:F1} s (index now {_index.Count():N0} items{(result.FullResync ? ", full resync" : "")})"); }
                _status = $"Up to date: {_index.Count():N0} items";
                idleSeconds = changes > 0 ? config.PollActiveSeconds : Math.Min(config.PollIdleMaxSeconds, idleSeconds * 2);

                if (!prefetchStarted)
                {
                    prefetchStarted = true;
                    _ = Task.Run(async () =>
                    {
                        try { await prefetcher.RunAsync(ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { Log.Error($"prefetch failed: {ex}"); }
                    }, CancellationToken.None);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (SignInRequiredException ex) { _status = "Sign-in required"; Log.Error(ex.Message); idleSeconds = config.PollIdleMaxSeconds; }
            catch (Exception ex)
            {
                _status = "Offline, will retry";
                Log.Warn($"sync round failed: {ex.Message}");
                idleSeconds = Math.Min(config.PollIdleMaxSeconds, Math.Max(30, idleSeconds * 2));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(idleSeconds), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public string Report()
    {
        var lat = CloudProvider.CallbackMicros.OrderBy(x => x).ToArray();
        string cb = lat.Length == 0 ? "no callbacks yet" : $"callbacks n={lat.Length} p50={lat[lat.Length / 2]:F0} us p99={lat[(int)(lat.Length * 0.99)]:F0} us max={lat[^1]:F0} us";
        using var p = Process.GetCurrentProcess();
        return $"{_status}; private {p.PrivateMemorySize64 / 1048576.0:F1} MB; {cb}; callers: {_callers.Summary()}";
    }

    private async Task ReportLoopAsync(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false); Log.Info("report: " + Report()); } }
        catch (OperationCanceledException) { }
    }

    private static void OpenBrowser(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"could not open the browser ({ex.Message}); open this URL manually: {uri.AbsoluteUri}"); }
    }

    public static void Shutdown()
    {
        PinWatcher.Stop();
        CloudProvider.Stop();
    }

    public void Dispose()
    {
        Shutdown();
        _auth?.Dispose();
        _http?.Dispose();
        _index?.Dispose();
    }
}
