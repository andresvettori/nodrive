// P0 step 2 spike. Variants: hello (hidden window + message loop), tray (adds a tray icon),
// http (tray + streamed download into the void, then the post-burst GC sequence). The file comes from
// --download <url> or --item <driveItemId> (signs in with the shared PKCE helper and fetches a fresh downloadUrl;
// pick an id from `graph-smoke --largest 10`). --limit-mb N stops after N MB so any big file works.
// Prints process stats every --sample N seconds (default 30). Quit via tray menu or Ctrl+C.

using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using NoDrive.Spikes.Shared;

string variant = "tray";
string? download = null;
string? itemId = null;
long limitBytes = long.MaxValue;
int sampleSeconds = 30;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--variant": variant = args[++i]; break;
        case "--download": download = args[++i]; break;
        case "--item": itemId = args[++i]; break;
        case "--limit-mb": limitBytes = long.Parse(args[++i], CultureInfo.InvariantCulture) * 1024 * 1024; break;
        case "--sample": sampleSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
    }
}

Console.WriteLine($"aot-baseline variant={variant} aot={IsAot()} gcServer={System.Runtime.GCSettings.IsServerGC} pid={Environment.ProcessId}");
Console.WriteLine(ProcessStats.Sample("start"));

var quit = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; TrayWindow.RequestQuit(); };

TrayWindow.Create("NoDriveAotBaseline", $"NoDrive aot-baseline ({variant})", withIcon: variant != "hello", onQuit: () => quit.Cancel());
ProcessStats.StartSampler(TimeSpan.FromSeconds(sampleSeconds));

if (variant == "http")
{
    _ = Task.Run(async () =>
    {
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            });
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ISV|NoDrive|NoDrive/0.0.1-spike");
            Console.WriteLine(ProcessStats.Sample("http-ready"));

            if (itemId is not null)
            {
                var auth = new SpikeAuth(http, SpikeAuth.DefaultTokenPath());
                string token = await auth.GetAccessTokenAsync();
                using var graph = GraphLite.NewClient(token);
                download = await GraphLite.GetDownloadUrlAsync(graph, itemId, quit.Token);
                Console.WriteLine($"downloadUrl fetched for item {itemId} (host {new Uri(download).Host})");
                Console.WriteLine(ProcessStats.Sample("after-auth"));
            }

            if (download is not null)
            {
                var sw = Stopwatch.StartNew();
                long total = 0;
                using var resp = await http.GetAsync(download, HttpCompletionOption.ResponseHeadersRead, quit.Token);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(quit.Token);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                try
                {
                    int read;
                    long nextReport = 100L * 1024 * 1024;
                    while (total < limitBytes && (read = await stream.ReadAsync(buffer.AsMemory(0, 1024 * 1024), quit.Token)) > 0)
                    {
                        total += read;
                        if (total >= nextReport)
                        {
                            Console.WriteLine(ProcessStats.Sample($"dl {total / 1048576} MB"));
                            nextReport += 100L * 1024 * 1024;
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                Console.WriteLine($"download done: {total / 1048576.0:F1} MB in {sw.Elapsed.TotalSeconds:F1} s ({total / 1048576.0 / sw.Elapsed.TotalSeconds:F1} MB/s)");
                Console.WriteLine(ProcessStats.Sample("after-dl"));
            }
            else
            {
                Console.WriteLine("no --download/--item given; http variant idles with a warm HttpClient only");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"download failed: {ex.Message}");
        }
        await ProcessStats.ReleaseAfterBurstAsync();
        Console.WriteLine("now idling; watch the sampler and measure.ps1. Quit from the tray menu.");
    });
}

TrayWindow.RunMessageLoop();
TrayWindow.Destroy();
Console.WriteLine(ProcessStats.Sample("exit"));
return 0;

static bool IsAot() => !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
