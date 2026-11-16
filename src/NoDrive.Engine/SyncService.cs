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
    private readonly SemaphoreSlim _signInRequested = new(0);
    private volatile bool _needsSignIn;

    /// <summary>True while the engine is waiting for the user to sign in (first run, timeout, or revoked/expired token).</summary>
    public bool NeedsSignIn => _needsSignIn;
    private LocalOps? _ops;
    private UploadService? _uploader;
    private long _itemCount;
    private DateTime _itemCountAt;

    /// <summary>Items in the local index (cached for ten seconds: counting is a table scan).</summary>
    public long ItemCount
    {
        get
        {
            if (_index is not null && DateTime.UtcNow - _itemCountAt > TimeSpan.FromSeconds(10)) { _itemCount = _index.Count(); _itemCountAt = DateTime.UtcNow; }
            return _itemCount;
        }
    }

    public long PendingUploads => _uploader?.Pending ?? 0;
    public int HeldDeletes => _ops?.HeldDeletes ?? 0;
    public void AllowDeletes() => _ops?.AllowDeletes();

    /// <summary>Tray action: (re)start the interactive sign-in. Safe to call any time.</summary>
    public void RequestSignIn() => _signInRequested.Release();

    /// <summary>Shows a fatal error in the tray instead of leaving a silent, apparently-alive process.</summary>
    public void MarkFailed(string reason) => _status = "Stopped: " + reason;

    public string Status => _status;
    public CallerStats Callers => _callers;

    /// <summary>A damaged index file must never keep NoDrive from starting: set it aside and rebuild from the cloud (placeholders on disk are untouched).</summary>
    private static ItemIndex OpenIndexOrRebuild(string path)
    {
        try { return new ItemIndex(path); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            Log.Error($"index unreadable ({ex.Message}); moving it aside and rebuilding");
            foreach (var suffix in new[] { "", "-wal", "-shm" }) { if (File.Exists(path + suffix)) { File.Move(path + suffix, $"{path}{suffix}.corrupt-{stamp}"); } }
            return new ItemIndex(path);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(paths.DataDir);
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());              // the system SQLite (System32\winsqlite3.dll)
        _index = OpenIndexOrRebuild(paths.IndexPath);
        _http = new HttpClient();
        _auth = new PkceAuthenticator(_http, new DpapiTokenStore(paths.TokenPath), OpenBrowser, config.ClientId, log: Log.Info);
        // Background work NEVER opens a browser: it fails fast with SignInRequiredException and the status flips. Only first launch and the tray action sign in.
        var graph = new GraphDrive(_http, tokenCt => _auth.GetAccessTokenAsync(tokenCt, allowInteractive: false), new ThrottleGate(), new GraphOptions());

        await EnsureSignedInAsync(autoPrompt: true, ct).ConfigureAwait(false);

        string rootId = _index.GetMeta("root.id") ?? await graph.GetRootIdAsync(ct).ConfigureAwait(false);
        _index.SetMeta("root.id", rootId);

        if (SyncRootRegistrar.IsInsideForeignSyncRoot(paths.SyncRoot, out string foreign)) { throw new InvalidOperationException($"{paths.SyncRoot} is inside the sync root of '{foreign}'. Choose another folder (config.json: syncRoot)."); }
        var (fresh, outcome) = await SyncRootRegistrar.EnsureRegisteredAsync(paths.SyncRoot, config.HydrationPolicy).ConfigureAwait(false);
        Log.Info($"sync root {paths.SyncRoot}: {outcome}; shell flags {SyncRootRegistrar.ReadFlags()}");
        NavigationPane.RemoveLegacyEntry();

        var population = new PopulationState(_index, rootId);
        if (fresh || !Directory.Exists(paths.SyncRoot)) { population.ResetAll(); }          // a new registration has no placeholders yet
        var locks = new FolderLocks();
        var policy = new HydrationPolicy();
        var hydrator = new Hydrator(graph, policy, new FailureMemory(policy.FailureMemory)) { Limiter = ActivityLog.Download };
        graph.UploadLimiter = ActivityLog.Upload;
        graph.OnBytesUploaded = ActivityLog.AddUp;
        ActivityLog.ApplyLimits(config);

        _indexReady = _index.GetMeta(DeltaIngestor.DeltaKey) is not null && _index.GetMeta(DeltaIngestor.NextKey) is null;
        CloudProvider.Start(paths.SyncRoot, new ProviderServices(_index, hydrator, population, locks, _callers, () => _indexReady, config.HydrationWorkers, new ReadAheadPlanner()));
        PinWatcher.Start(paths.SyncRoot);
        _ = Task.Run(() => ReportLoopAsync(ct), CancellationToken.None);

        var prefetcher = new Prefetcher(_index, population, locks, paths.SyncRoot, config.ToPopulationOptions());
        var applier = new RemoteApplier(_index, population, locks, paths.SyncRoot, rootId);
        var ingestor = new DeltaIngestor(graph, _index) { BeforePage = (changes, full) => _indexReady && !full ? applier.Before(changes) : null, OnProgress = (pages, items) => _status = $"Indexing OneDrive: {items:N0} items" };
        int idleSeconds = config.PollActiveSeconds;
        bool prefetchStarted = false;
        _ops = new LocalOps(_index, graph, paths.SyncRoot, rootId);
        var uploader = _uploader = new UploadService(_index, graph, population, paths.SyncRoot, rootId);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (UserSettings.Paused && !ct.IsCancellationRequested) { _status = "Paused"; await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                var sw = Stopwatch.StartNew();
                var result = await ingestor.RunAsync(ct).ConfigureAwait(false);
                bool first = !_indexReady;
                _indexReady = true;
                long changes = result.Upserts + result.Deletions;
                if (changes > 0) { ActivityLog.Add("sync", "OneDrive", "ok", detail: $"{result.Upserts} changed, {result.Deletions} deleted"); }
                if (first || changes > 0) { Log.Info($"delta: {result.Pages} pages, {result.Upserts:N0} upserts, {result.Deletions:N0} deletions in {sw.Elapsed.TotalSeconds:F1} s (index now {_index.Count():N0} items{(result.FullResync ? ", full resync" : "")})"); }
                _status = $"Up to date: {_index.Count():N0} items";
                idleSeconds = changes > 0 ? config.PollActiveSeconds : Math.Min(config.PollIdleMaxSeconds, idleSeconds * 2);

                if (!prefetchStarted)
                {
                    prefetchStarted = true;
                    uploader.Start(ct);
                    _ops.Start(ct);
                    _ = Task.Run(async () =>
                    {
                        try { await prefetcher.RunAsync(ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { Log.Error($"prefetch failed: {ex}"); }
                    }, CancellationToken.None);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (SignInRequiredException ex)
            {
                Log.Warn(ex.Message);
                await EnsureSignedInAsync(autoPrompt: false, ct).ConfigureAwait(false);     // token revoked/expired mid-run: wait for the user, then continue
                idleSeconds = config.PollActiveSeconds;
            }
            catch (Exception ex)
            {
                _status = "Offline, will retry";
                ActivityLog.Add("error", "OneDrive", "failed", detail: ex.Message);
                Log.Warn($"sync round failed: {ex.Message}");
                idleSeconds = Math.Min(config.PollIdleMaxSeconds, Math.Max(30, idleSeconds * 2));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(idleSeconds), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }


    /// <summary>
    /// Returns once a valid token exists. Silent refresh first; if the user must act, open the browser once automatically on a first
    /// launch (autoPrompt), otherwise wait for the tray's "Sign in..." action. A timed-out or failed attempt leaves the engine running
    /// and waiting, never stuck or stopped.
    /// </summary>
    private async Task EnsureSignedInAsync(bool autoPrompt, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _status = "Signing in";
                await _auth!.GetAccessTokenAsync(ct, allowInteractive: false).ConfigureAwait(false);
                if (_needsSignIn) { Log.Info("signed in"); }
                _needsSignIn = false;
                return;
            }
            catch (SignInRequiredException) { /* user action needed: fall through */ }

            if (autoPrompt)
            {
                autoPrompt = false;                                                        // only once per run
                Log.Info("first sign-in: opening the browser");
                try
                {
                    _status = "Signing in (finish in the browser)";
                    await _auth!.GetAccessTokenAsync(ct, allowInteractive: true).ConfigureAwait(false);
                    _needsSignIn = false;
                    Log.Info("signed in");
                    return;
                }
                catch (SignInRequiredException ex) { Log.Warn(ex.Message); }
            }

            _needsSignIn = true;
            _status = "Sign-in required: use the tray menu > Sign in...";
            Log.Warn("waiting for the user to sign in (tray menu > Sign in...)");
            await _signInRequested.WaitAsync(ct).ConfigureAwait(false);
            autoPrompt = true;                                                             // the user asked: run the interactive flow now
        }
        ct.ThrowIfCancellationRequested();
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
