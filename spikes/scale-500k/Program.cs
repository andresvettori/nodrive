// P0 steps 5-6: SQLite index + fake Graph delta server, 500k items. Commands:
//   scale-500k run     [--items 500000] [--page-size 1000] [--db <path>] [--stop-after <pages>] [--keep]
//   scale-500k serve   --items N --page-size N --port P
// `run` starts the fake server as a SEPARATE process (so only the client is measured), ingests, lists the biggest folder,
// does random lookups, applies an incremental change set, tests resume after interruption, and prints a summary.

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using NoDrive.Spikes.Scale;
using NoDrive.Spikes.Shared;

string cmd = args.Length > 0 ? args[0] : "run";
int items = 500_000, pageSize = 1000, port = 0, stopAfter = int.MaxValue;
string? db = null;
bool keep = false;
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--items": items = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--page-size": pageSize = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--port": port = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--db": db = args[++i]; break;
        case "--stop-after": stopAfter = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--keep": keep = true; break;
    }
}

if (cmd == "serve")
{
    var server = new FakeGraphServer(items, pageSize, port);
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    server.Run(cts.Token);
    return 0;
}

// ---------------- run ----------------
ItemStore.InitProvider();
db ??= Path.Combine(Path.GetTempPath(), $"nodrive-scale-{items}.db");
foreach (var f in new[] { db, db + "-wal", db + "-shm" }) { if (File.Exists(f)) { File.Delete(f); } }
port = FreePort();

Console.WriteLine($"scale-500k aot={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported} os={Environment.OSVersion} items={items} pageSize={pageSize} db={db}");
using var serverProc = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"serve --items {items} --page-size {pageSize} --port {port}")
{ RedirectStandardOutput = true, UseShellExecute = false })!;
Console.WriteLine("server: " + serverProc.StandardOutput.ReadLine());

var peak = new PeakSampler();
Console.WriteLine(ProcessStats.Sample("start"));

using var http = new HttpClient(new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30) });
string baseUrl = $"http://127.0.0.1:{port}";

// ---- 1. Full delta ingestion, with an interruption in the middle to prove resume ----
int firstRunStop = Math.Min(stopAfter, items / pageSize / 3);
long fullBytes = 0;
var ingest = Stopwatch.StartNew();
var store = new ItemStore(db);
int pages1 = await IngestAsync(store, http, $"{baseUrl}/delta?page=0", firstRunStop);
Console.WriteLine($"interrupted after {pages1} pages: rows={store.Count()} resume token stored={(store.GetMeta("next") is not null)}");
store.Dispose();

store = new ItemStore(db);                       // "restart": reopen the database, resume from the stored nextLink
string resume = store.GetMeta("next") ?? throw new InvalidOperationException("no resume token");
int pages2 = await IngestAsync(store, http, resume, int.MaxValue);
ingest.Stop();
long total = store.Count();
Console.WriteLine($"INGEST: {total} rows in {pages1 + pages2} pages, {ingest.Elapsed.TotalSeconds:F1} s ({total / ingest.Elapsed.TotalSeconds:N0} rows/s), bytes parsed {fullBytes / 1048576.0:F0} MiB");
Console.WriteLine($"        peak private during ingest {peak.PeakPrivateMb:F1} MB, peak ws {peak.PeakWsMb:F1} MB; expected rows {items} -> {(total == items ? "OK" : "MISMATCH")}");
store.Checkpoint();
double dbMb = new FileInfo(db).Length / 1048576.0;
Console.WriteLine($"        database file {dbMb:F0} MB ({dbMb * 1048576 / total:F0} bytes/row incl. indexes)");
Console.WriteLine(ProcessStats.Sample("post-ingest"));
await ProcessStats.ReleaseAfterBurstAsync();
double settledAfterIngest = Settled();
peak.Reset();

// ---- 2. FETCH_PLACEHOLDERS simulation: list the biggest folder from the index ----
string? big = store.BiggestFolder(out long bigChildren);
var (rows, firstMs, totalMs) = store.ListChildren(big!);
Console.WriteLine($"LIST: biggest folder {big} has {bigChildren} children; streamed {rows} rows; first 512 rows in {firstMs:F1} ms, all in {totalMs:F1} ms; peak private {peak.PeakPrivateMb:F1} MB");
var small = store.ListChildren(FakeGraphServer.IdOf(3));
Console.WriteLine($"      small folder: {small.rows} rows in {small.totalMs:F2} ms");

// ---- 3. Random point lookups (what a callback does: FileIdentity -> row) ----
var sample = store.SampleIds(5000);
double lookupMs = store.RandomLookups(sample, 100_000);
Console.WriteLine($"LOOKUP: 100,000 random id lookups in {lookupMs:F0} ms ({lookupMs * 1000 / 100_000:F1} us each)");

// ---- 4. Incremental delta: 3,000 modified + 1,000 new + 1,000 deleted ----
var inc = Stopwatch.StartNew();
long before = store.Count();
using (var resp = await http.GetAsync($"{baseUrl}/delta?since=token1", HttpCompletionOption.ResponseHeadersRead))
{
    resp.EnsureSuccessStatusCode();
    await using var stream = await resp.Content.ReadAsStreamAsync();
    var page = (await JsonSerializer.DeserializeAsync(stream, ScaleJsonContext.Default.DeltaPage))!;
    var (ups, dels) = store.ApplyPage(page, "delta", page.DeltaLink!);
    Console.WriteLine($"INCREMENTAL: {ups} upserts + {dels} deletes in {inc.ElapsedMilliseconds} ms; rows {before} -> {store.Count()} (expected {before + 1000 - 1000})");
}

// ---- 5. Idle after everything ----
store.Checkpoint();
await ProcessStats.ReleaseAfterBurstAsync();
Console.WriteLine($"SETTLED: private {Settled():F1} MB after ingest of {total} items (post-ingest settle was {settledAfterIngest:F1} MB); overall peak private {peak.OverallPeakPrivateMb:F1} MB");
Console.WriteLine(ProcessStats.Sample("final"));

store.Dispose();
peak.Stop();
try { serverProc.Kill(); } catch { }
if (!keep) { foreach (var f in new[] { db, db + "-wal", db + "-shm" }) { try { File.Delete(f); } catch { } } }
return total == items ? 0 : 2;

async Task<int> IngestAsync(ItemStore s, HttpClient client, string url, int maxPages)
{
    int n = 0;
    string? next = url;
    while (next is not null && n < maxPages)
    {
        using var resp = await client.GetAsync(next, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        fullBytes += resp.Content.Headers.ContentLength ?? 0;
        await using var stream = await resp.Content.ReadAsStreamAsync();
        var page = (await JsonSerializer.DeserializeAsync(stream, ScaleJsonContext.Default.DeltaPage))!;
        string resumeFrom = page.NextLink ?? page.DeltaLink!;
        s.ApplyPage(page, page.NextLink is not null ? "next" : "delta", resumeFrom);
        next = page.NextLink;
        n++;
        if (n % 100 == 0) { Console.WriteLine($"  page {n}: {ProcessStats.Sample("ingest").Split("  gcCommitted")[0]}"); }
    }
    return n;
}

static double Settled()
{
    using var p = Process.GetCurrentProcess();
    p.Refresh();
    return p.PrivateMemorySize64 / 1048576.0;
}

static int FreePort()
{
    var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    l.Start();
    int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return port;
}

internal sealed class PeakSampler
{
    private readonly Thread _t;
    private volatile bool _stop;
    private double _peakPrivate, _peakWs, _overall;
    public double PeakPrivateMb => _peakPrivate;
    public double PeakWsMb => _peakWs;
    public double OverallPeakPrivateMb => Math.Max(_overall, _peakPrivate);
    public PeakSampler()
    {
        _t = new Thread(() =>
        {
            using var p = Process.GetCurrentProcess();
            while (!_stop)
            {
                p.Refresh();
                double priv = p.PrivateMemorySize64 / 1048576.0, ws = p.WorkingSet64 / 1048576.0;
                if (priv > _peakPrivate) { _peakPrivate = priv; }
                if (ws > _peakWs) { _peakWs = ws; }
                Thread.Sleep(100);
            }
        })
        { IsBackground = true, Name = "peak-sampler" };
        _t.Start();
    }
    public void Reset() { _overall = Math.Max(_overall, _peakPrivate); _peakPrivate = 0; _peakWs = 0; }
    public void Stop() => _stop = true;
}
