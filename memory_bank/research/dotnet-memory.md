# Research: low-footprint .NET for the NoDrive engine

Verified knowledge base from the "memory" topic of the 2026-11-13 research pass (researcher report + adversarial verdict, overall reliability: high) plus the related gaps from the completeness critique. Every claim carries a source URL or is labelled `community-reported` / `to verify in spike`. Facts the skeptics corrected are stated here in their corrected form.

Why this matters for NoDrive: OneDrive.exe reached almost 4 GB on the user's 8 GB PC and froze Explorer. The engine process must have an idle footprint independent of item count, release memory after every transfer burst, and never do blocking work on a cfapi callback thread. Memory rules below feed `bestPractices.md` and the P0 acceptance tests (idle under 30 MB Private Bytes with a 500k-item index; commit back to within 10 MB of idle within 60 s after a 10 GB hydration burst).

---

## Measured in CI (2026-11-13, windows-latest, .NET SDK 11.0.100, ILCompiler 11.0.0)

- Native AOT publish of `net11.0-windows10.0.26100.0` projects works: engine stub 0.78 MB; `aot-baseline` (message loop + tray + HttpClient + JsonNode) 4.41 MB; `cfapi-minimal` (adds WinRT `Windows.Storage.Provider` projection via CsWinRT and the Cloud Files callbacks) 6.06 MB. Zero IL2xxx/IL3xxx warnings from any project, so CsWinRT's projection for the .NET 11 TFM is AOT-clean in this configuration.
- ILCompiler warns and ignores `runtimeconfig.template.json`; GC knobs must be `RuntimeHostConfigurationOption` items (now in `eng/LowFootprint.props`).

## 1. .NET 11 status (as of 2026-11-13)

- .NET 11 is GA since 2026-11-10, a **Standard Term Support** release, end of support **2028-11-14**. .NET 10 LTS (released November 2025) has the **same** end date. .NET 8 and 9 went out of support 2026-11-10. Choosing net11.0 vs net10.0 does not change the support horizon; it is a features-vs-maturity call only.
  - https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
  - https://devblogs.microsoft.com/dotnet/?p=61205 (Announcing .NET 11)
  - https://devblogs.microsoft.com/dotnet/dotnet-sts-releases-supported-for-24-months/
- Hardware baseline raised to **x86-64-v2** for JIT and AOT (R2R targets x86-64-v3 on Windows/Linux; Windows Arm64 requires LSE). Pre-2013 CPUs fail with "The current CPU is missing one or more of the baseline instruction sets." Irrelevant for the user's PC but document it for any distribution.
  - https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime
- **Runtime Async** is a preview feature; it supports NativeAOT. Enable with `<Features>runtime-async=on</Features>`, opt out with `<UseRuntimeAsync>false</UseRuntimeAsync>`. Nuance: the BCL itself is compiled with runtime-async=on, so opting out only affects our code; BCL async paths run in the new model regardless. The `DOTNET_RuntimeAsync` env var was removed. Decision: leave it off for v1 (stability on an STS release), accept that the BCL uses it.
  - https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime
- NativeAOT interface dispatch goes through a shared helper (smaller call sites, better throughput). `Assembly.GetCallingAssembly()` works on NativeAOT when stack trace data is present.
- `StackTraceSupport=false` now makes stack-trace-dependent APIs throw `NotSupportedException` (.NET 11+). New PublishAot-only `StackTraceLineNumberSupport` embeds file/line info.
  - https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trimming-options
- SDK: the `dotnet` CLI itself ships as a NativeAOT host by default (opt out `DOTNET_CLI_ENABLEAOT=false`); this affects CLI startup only, not our app. `UseOptimizedPublish` (skip the full build chain for AOT publish) is **opt-in**: `<PublishAot>true</PublishAot><UseOptimizedPublish>true</UseOptimizedPublish>`.
  - https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/sdk
- No GC algorithm changes listed for .NET 11 beyond a Windows multi-CPU-group correctness fix. .NET 10 added JIT stack allocation / escape analysis (whether ILC gets the same benefit is an open question, see section 11).
  - https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/runtime

---

## 2. Native AOT

### What breaks
- Official limitations (overview updated 2026-11-05): no dynamic assembly loading, no `Reflection.Emit`, no C++/CLI, **Windows: no built-in COM**, requires trimming, implies single file, `System.Linq.Expressions` always interpreted, struct-generic instantiations specialised per type (binary size), not all runtime libraries fully annotated. Prerequisite on Windows: VS 2022+ with "Desktop development with C++". Windows x64/Arm64/x86 supported since .NET 9.
  - https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/
- **WinForms**: dotnet/winforms #4649 "Epic - Make WinForms trim compatible", open, milestone Future, 2,535 trim warnings, updated 2026-09-14. **WPF**: dotnet/wpf #3811 "WPF is not trim-compatible", open, milestone Future, updated 2025-11-05. Neither goes in the engine process. (lezzi/wpfaot is an unofficial PoC, ignore.)
  - https://api.github.com/repos/dotnet/winforms/issues/4649
  - https://api.github.com/repos/dotnet/wpf/issues/3811
- **System.Text.Json reflection**: "calling a basic serialization method can break Native AOT apps"; under PublishTrimmed reflection serialization is auto-disabled. Non-generic `JsonStringEnumConverter` is unsupported; use `JsonStringEnumConverter<TEnum>`. See section 6.
  - https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation
- **MSAL**: core `Microsoft.Identity.Client` declares `IsAotCompatible` since 4.77.1 (skeptic correction; the graph verdict inspected `src/Directory.Build.props`). The **Broker/WAM package** (`Microsoft.Identity.Client.Broker` + `NativeInterop` msalruntime, netstandard2.0, about 21 MB) does **not**, has an open AOT bug, and cannot be bundled into a single-file AOT exe. Changelog facts: 4.48.0 trimmable + STJ source-gen; 4.58.0 "Fixed an issue with the Windows broker dependencies when the app was targeting NativeAOT on Windows"; 4.84.0 Newtonsoft removed; latest 4.92.0 (NativeInterop 0.20.7). Confirmed decision: hand-rolled PKCE + system browser + DPAPI, no MSAL in v1.
  - https://raw.githubusercontent.com/AzureAD/microsoft-authentication-library-for-dotnet/main/CHANGELOG.md
- **System.Drawing.Common**: csproj sets `<EnableAOTAnalyzer>false</EnableAOTAnalyzer>`, no `IsAotCompatible`. Any tray library depending on it (SimpleTrayIcon 3.1.10, MIT, claims "fully AOT compatible") carries unanalysed risk. Load the tray `.ico` via `LoadImageW`/`LoadIconW` from CsWin32 instead.
  - https://raw.githubusercontent.com/dotnet/winforms/main/src/System.Drawing.Common/src/System.Drawing.Common.csproj
  - https://www.nuget.org/packages/SimpleTrayIcon
- **COM for shell extensions** (thumbnail/status handlers, P2): use `[GeneratedComInterface]`/`[GeneratedComClass]` (ComWrappers source generation, .NET 8+), "trimmable and AOT-friendly", IUnknown-based interfaces only, several `Marshal` APIs unsupported.
  - https://learn.microsoft.com/en-us/dotnet/standard/native-interop/comwrappers-source-generation
- **Microsoft.Graph SDK**: 52 MB nupkg, IsTrimmable but not IsAotCompatible; Kiota 1.16 removed LINQ so it "no longer crashes the AOT compiler" (community-reported); no official AOT statement. Confirmed decision: no Graph SDK.
  - https://github.com/dotnet/runtime/issues/102131
- **Microsoft.Extensions.Hosting**: pulls about 16 Microsoft.Extensions packages (Configuration.*, Logging.Console/Debug/EventLog/EventSource, FileProviders.Physical with file watchers). DI is documented trim/AOT safe (dotnet/runtime #79003); the worker template has an AOT test. AOT status of the Hosting package itself is **unverified** (skeptic: unverifiable). Decision: hand-rolled main loop, STJ-read settings file, custom lightweight log sink; no Hosting/Logging stack in the engine.
  - https://github.com/dotnet/runtime/issues/79003
  - https://learn.microsoft.com/en-us/dotnet/core/extensions/workers
- **Microsoft.Data.Sqlite**: AOT status not officially stated (efcore #29725 trim warnings closed not-planned). `SQLitePCLRaw.bundle_winsqlite3` / `provider.winsqlite3` uses the system `winsqlite3.dll` (zero native payload). Under AOT call `SQLitePCL.Batteries.Init()` / `SetProvider` explicitly. Fallbacks: raw `LibraryImport` to winsqlite3 (about 15 functions) or a custom binary index. To verify in spike (P0 step 5).
  - https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions
  - https://github.com/dotnet/efcore/issues/29725

### Publish properties (engine csproj starting point)
From the Native AOT optimizing page and the trimming options table (updated 2026-08-24). Each MSBuild property also writes the matching runtimeconfig feature switch.

| Property | Value | Note |
|---|---|---|
| `PublishAot` | true | `dotnet publish -r win-x64 -c Release` |
| `OptimizationPreference` | Size | documented name (`IlcOptimizationPreference` is not in current docs) |
| `InvariantGlobalization` | true | removes ICU dependency and globalization data |
| `UseSystemResourceKeys` | true | strips System.* exception messages |
| `EventSourceSupport` | false | |
| `MetricsSupport` | false | |
| `HttpActivityPropagationSupport` | false | |
| `Http3Support` | false | .NET 10+, trims HTTP/3 |
| `XmlResolverIsNetworkingEnabledByDefault` | false | |
| `JsonSerializerIsReflectionEnabledByDefault` | false | |
| `UseSizeOptimizedLinq` | (default true under PublishAot) | .NET 10+ |
| `StackTraceSupport` | **true** | keep: .NET 11 throws NotSupportedException in dependent APIs when false; we want crash stacks |
| `DebuggerSupport` | **true** for shipping | false also sets TrimmerRemoveSymbols=true, i.e. no .pdb for crash-dump analysis of the resident process |
| `ConcurrentGarbageCollection` | false | section 3 |
| `ServerGarbageCollection` | false | section 3 |
| `TieredPGO` | false | irrelevant under AOT, harmless |
| `TrimmerSingleWarn` | false (diagnostics) | per-warning detail; add `ILLinkTreatWarningsAsErrors` once clean |
| `DisableRuntimeMarshalling` + `AllowUnsafeBlocks` | true | required by CsWin32 AOT route |
| `UseOptimizedPublish` | true (optional) | .NET 11 opt-in faster AOT publish |

Features disabled by trimming by default: `BuiltInComInteropSupport`, `CustomResourceTypesSupport`, `EnableCppCLIHostActivation`, `StartupHookSupport`.
- https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/optimizing
- https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trimming-options
- https://raw.githubusercontent.com/dotnet/runtime/main/docs/workflow/trimming/feature-switches.md

### CsWin32 under AOT
If `PublishAot`, `DisableRuntimeMarshalling` or `PublishTrimmed` is set, either `<CsWin32RunAsBuildTask>true</CsWin32RunAsBuildTask>` (emits `LibraryImport`/`GeneratedComInterface` code; "strongly recommended" with `DisableRuntimeMarshalling=true`) or `"allowMarshaling": false` in `NativeMethods.json`. Needs .NET 8 SDK+, unsafe code enabled.
- https://microsoft.github.io/CsWin32/docs/getting-started.html

### IsAotCompatible metadata and VerifyReferenceAotCompatibility
- `IsAotCompatible=true` on a library turns on IsTrimmable, EnableTrimAnalyzer, EnableSingleFileAnalyzer, EnableAotAnalyzer.
- The **assembly metadata was introduced in .NET 10**, so `VerifyReferenceAotCompatibility=true` emits IL3058 for every library built for net8.0/net9.0 (MSAL, SimpleTrayIcon, most current packages) regardless of real behaviour. Use it as an **inventory** of unannotated references, not as a gate; the gate is a real `PublishAot` run with zero IL2xxx/IL3xxx from our code.
  - https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/

### Symbols and debugging
Debug info goes to a separate `.pdb` on Windows. Keep `DebuggerSupport=true` and ship the `.pdb` alongside (or archive it per build) so crash dumps from the always-on engine are symbolicated. Trade-off: slightly larger binary and more metadata vs the ability to diagnose a hang in the field. Given the never-hang requirement, diagnosability wins.
- https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trimming-options

### No cross-OS compile
"Native AOT does not support cross-OS compilation." A win-x64 AOT binary must be produced on Windows (physical PC, VM, or GitHub Actions `windows-latest`); WSL is Linux and does not count. Cross-architecture x64/arm64 on the same OS works with the VS C++ build tools. Unofficial Zig-linker packages (StuDev.AotAnywhere, Fallenwood.PublishAotCross.Extensions) exist but are unsupported. Build host decision: the user's physical Windows 11 x64 PC; CI optional.
- https://learn.microsoft.com/dotnet/core/deploying/native-aot/cross-compile
- https://www.nuget.org/packages/StuDev.AotAnywhere

---

## 3. GC configuration

Settings are read once at GC initialisation (runtimeconfig / env), except latency mode and LOH compaction mode which are API-settable. runtimeconfig values are decimal; `DOTNET_GC*` env values are hex. There is no MSBuild property for most of these: use `<ItemGroup><RuntimeHostConfigurationOption Include="System.GC.ConserveMemory" Value="5" /></ItemGroup>`.

| Setting | Value for NoDrive | Why | Source |
|---|---|---|---|
| `System.GC.Server` | false (default, workstation) | one small heap; DATAS and other server-GC features "have no effect on Workstation GC" | https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector |
| `System.GC.Concurrent` / `ConcurrentGarbageCollection` | **false** | no dedicated background GC thread; Batch latency mode becomes default ("for applications that have no UI"); Maoni: "While BGC is good for pause time, it isn't good for shrinking the heap" | https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/latency ; https://raw.githubusercontent.com/Maoni0/mem-doc/master/doc/.NETMemoryPerformanceAnalysis.md |
| `GCSettings.LatencyMode` | leave Batch; never SustainedLowLatency | SustainedLowLatency "results in a larger managed heap size" and "cannot be used if background garbage collection is disabled" | https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/latency |
| `System.GC.ConserveMemory` | **5** to start, tune 5-7 | 1-9; compacts when gen2 fragmentation is high (Maoni: value n maps roughly to a (10-n)*10 % fragmentation threshold); non-zero also auto-compacts LOH; docs: "Start with a value between 5 and 7" | https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector |
| `System.GC.RegionSize` | **1048576** (1 MB) | "For processes that have very small GC heaps, changing the region size to be smaller is beneficial for native memory usage from GC's own bookkeeping. The recommendation is 1 MB"; default 4 MB SOH, UOH 8x. runtimeconfig name is .NET 10+ (env `DOTNET_GCRegionSize` since .NET 7) | same |
| `System.GC.RegionRange` | default (256 GB reserve, not committed); becomes 5x HeapHardLimit when a single hard limit is set | reserve only, no commit cost | same |
| `System.GC.HeapHardLimit` | set once the P0 peak is known (research suggested 134217728 = 128 MB as a placeholder) | "maximum commit size, in bytes, for the GC heap and GC bookkeeping"; 64-bit only; exceeding it throws OOM, which is exactly the **fail-loudly** behaviour the plan wants instead of a 4 GB balloon. Needs a crash handler that logs and restarts the engine cleanly (design rule 9) | same |
| `System.GC.HighMemoryPercent` | consider lowering from the 90 % default on 8 GB machines | makes the GC compact sooner when the machine is under pressure | same |
| `System.GC.RetainVM` | false (default) | "Release segments back to the operating system" | same |
| `System.GC.Gen0MaxBudget` | do not set | Maoni: "you might set it too small which would trigger GCs too often" | https://raw.githubusercontent.com/dotnet/runtime/main/src/coreclr/gc/gcconfig.h |
| `DOTNET_GCgen0size` | do not rely on it | private env var only ("smallest gen0 budget"), public name NULL; semantics for tiny heaps are an open question | same |
| DATAS (`GCDynamicAdaptationMode`) | not applicable | default on since .NET 9 but server GC only, "always starts with 1 heap" | https://devblogs.microsoft.com/dotnet/preparing-for-dotnet-10-gc/ |

No Microsoft-published measurement quantifies these knobs for a tiny workstation heap: tune against the P0 numbers (section 8).

### Releasing memory after a burst (correct call shape)
- `GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive)` (.NET 7+): "Requests that the garbage collector decommit as much memory as possible." It is a **blocking, compacting full GC**.
- Overload semantics (from `GC.CoreCLR.cs`): the 2- and 3-arg overloads compute `aggressive = generation == MaxGeneration && mode == Aggressive` and pass `compacting: aggressive`. The explicit 4-arg `GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: false)` throws `ArgumentException`; so does a generation other than MaxGeneration. Setting `GCSettings.LargeObjectHeapCompactionMode = CompactOnce` first is redundant with Aggressive (already compacting).
  - https://learn.microsoft.com/en-us/dotnet/api/system.gccollectionmode?view=net-10.0
  - https://learn.microsoft.com/en-us/dotnet/api/system.gc.collect?view=net-11.0
  - https://raw.githubusercontent.com/dotnet/runtime/main/src/coreclr/System.Private.CoreLib/src/System/GC.CoreCLR.cs
  - https://www.infoq.com/news/2022/11/GCCollectionMode-Aggressive
- **.NET 10 UOH decommit ageing**: free LOH/POH regions now age out over 2 gen2 GCs and the 3rd puts them on the decommit list; a microbenchmark that used to drop memory after one call "now requires 3 GC.Collect() calls". The blog does not say whether this applies to workstation GC specifically; assume yes and verify with `GC.GetGCMemoryInfo().TotalCommittedBytes`. Pattern: after a transfer batch, dispose streams and return pooled buffers, then one Aggressive collect; if commit has not dropped after a few seconds, a second (and at most third) spaced collect.
  - https://devblogs.microsoft.com/dotnet/preparing-for-dotnet-10-gc/
- Design note (inference, not from a source): the Aggressive collect is blocking, so run it only from the burst-completion path when no cfapi request is in flight, never on a timer, and never on a thread that holds a callback or a CfExecute in progress. With a heap of a few MB the pause is milliseconds, but the rule keeps callback latency independent of GC activity.
- Working-set trimming (`EmptyWorkingSet`, `SetProcessWorkingSetSize(-1,-1)`) only moves pages to the standby list, does not reduce commit/private bytes, and Microsoft warns it "can also lead to failures of operations that require physical memory to be present". At most a one-time cosmetic trim right after a burst; never periodic. Measure **Private Bytes / Commit**, not the Task Manager "Memory" column alone.
  - https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-emptyworkingset
  - https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-setprocessworkingsetsizeex
  - https://wiert.me/2021/07/07/setprocessworkingsetsize-you-hardly-if-ever-need-to-call-this-from-your-process/ (community-reported)

---

## 4. Process patterns

### Engine: headless WinExe + message-only window + Shell_NotifyIcon
- `OutputType=WinExe` (no console window), no UI framework. Main thread runs a classic `GetMessageW`/`TranslateMessage`/`DispatchMessageW` loop on a message-only window (`HWND_MESSAGE` parent); all cfapi, watcher and Graph work happens on worker tasks. A few hundred lines, essentially no runtime footprint beyond the window.
- CsWin32 `NativeMethods.txt`: `Shell_NotifyIconW`, `RegisterClassExW`, `CreateWindowExW`, `DefWindowProcW`, `GetMessageW`, `TranslateMessage`, `DispatchMessageW`, `RegisterWindowMessageW`, `CreatePopupMenu`, `TrackPopupMenuEx`, `LoadImageW`, plus the `Cf*` set. `NativeMethods.json`: `{ "allowMarshaling": false, "useSafeHandles": true }`.
- Handle both `WM_CONTEXTMENU` (keyboard-invoked) and `WM_RBUTTONUP`; re-add the icon on the `RegisterWindowMessageW("TaskbarCreated")` broadcast after an Explorer restart (Explorer restarts are expected during cfapi testing). Handle `WM_QUERYENDSESSION` for graceful `CfDisconnectSyncRoot`.
  - https://learn.microsoft.com/sv-se/windows/win32/api/shellapi/nf-shellapi-shell_notifyicona
  - https://source.dot.net/System.Windows.Forms.Primitives/Windows/Win32/PInvoke.Shell_NotifyIconW.cs.html (WinForms' own CsWin32-generated binding, shape reference)
  - https://static.codeproject.com/Articles/5287729/A-WPF-Friendly-Shell-NotifyIcon-Wrapper-Class-Pa-1 (community-reported, proxy-window pattern)
- Alternatives surfaced: SimpleTrayIcon 3.1.10 (System.Drawing.Common dependency, see section 2), NativeTray, YDKK.Windows.NotifyIcon (CsWin32-style), H.NotifyIcon (WPF/WinUI/MAUI, not applicable).
  - https://github.com/YDKK/YDKK.Windows.NotifyIcon ; https://www.nuget.org/packages/NativeTray

### Settings: separate on-demand process
Anything with a real UI runs in `NoDrive.Settings.exe`, launched on demand and exiting when closed, so its UI framework footprint is reclaimed entirely. .NET 11 Process APIs that make this clean (libraries what's-new, updated 2026-11-12):
- `Process.StartAndForget` - "when you don't intend to wait for it; the runtime detaches the handle automatically".
- `ProcessStartInfo.StartDetached` - "detaches from the parent's session/console so the child can outlive a terminal exit".
- `ProcessStartInfo.KillOnParentExit` (Windows only) - child terminated when parent exits; use for the settings window so an engine restart does not orphan it.
- `ProcessStartInfo.StartSuspended` + `SafeProcessHandle.Resume()`.
- Redirected stdout/stderr now use overlapped I/O on Windows (less thread-pool blocking) if the two processes talk over pipes.
  - https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries

### Thread pool: Windows pool vs portable pool
- Native AOT on Windows uses the **Windows thread pool by default**. There `ThreadPool.SetMinThreads/SetMaxThreads` and `BindHandle(SafeHandle)` are unsupported and min/max config is ineffective; thread lifetime is Windows' policy. Switch with `UseWindowsThreadPool=false` (runtimeconfig `System.Threading.ThreadPool.UseWindowsThreadPool`, env `DOTNET_ThreadPool_UseWindowsThreadPool`).
- The portable .NET pool retires idle threads after 20 s (`DefaultThreadPoolThreadTimeoutMs = 20 * 1000`, configurable via `System.Threading.ThreadPool.ThreadTimeoutMs`, -1 disables). This constant does **not** apply on the default AOT path (skeptic correction of the original report's reasoning). Which pool yields fewer resident threads/commit at idle is unmeasured: record thread count under both settings in P0.
- Default thread stack 1.5 MB on Windows (reserved, committed on touch); `System.Threading.DefaultStackSize` (.NET 10+, 64 KB-2 GB) bounds commit for runtime-created threads.
  - https://learn.microsoft.com/en-us/dotnet/core/runtime-config/threading
  - https://raw.githubusercontent.com/dotnet/runtime/main/src/libraries/System.Private.CoreLib/src/System/Threading/PortableThreadPool.cs
- Relevance to never-blocking Explorer: cfapi callbacks arrive on arbitrary pool threads; with the Windows pool we cannot raise the minimum thread count, so callbacks must stay sub-millisecond and hand off to our own bounded worker queue (design rule 1) rather than relying on pool growth.

---

## 5. Networking

- One long-lived `HttpClient` over a single `SocketsHttpHandler`:
  - `PooledConnectionIdleTimeout` default is 1 minute (.NET 6+); set **20-30 s** so idle TLS sockets and their buffers go away soon after a sync cycle.
  - `PooledConnectionLifetime` about 5 minutes (DNS refresh), `MaxConnectionsPerServer` 4-8 (matches the fixed hydration/upload concurrency), `AutomaticDecompression` only if needed.
  - https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.pooledconnectionidletimeout?view=net-10.0
- `SocketsHttpHandler.ShouldEvictConnection` (.NET 11, `Func<SocketsHttpConnectionEvictionContext, CancellationToken, Task<bool>>`) is **`[Experimental("SYSLIB5008")]`**: requires `NoWarn`/pragma and may change. Do not use in v1; the non-experimental levers (idle timeout, lifetime, or disposing the handler at the end of a cycle) are enough. `InitialHttp2MaxConcurrentStreams` is new too (not independently verified beyond the heading).
  - https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.shouldevictconnection?view=net-11.0
  - https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries
- Streaming downloads (hydration): `SendAsync(req, HttpCompletionOption.ResponseHeadersRead)` then `ReadAsStreamAsync` and copy in fixed-size `ArrayPool<byte>.Shared.Rent` buffers straight into `CfExecute TRANSFER_DATA` (or a `FileStream` with `FileOptions.Asynchronous | SequentialScan` for non-placeholder paths). Never `ReadAsByteArrayAsync` / `MemoryStream` for file bodies. Return buffers immediately after each chunk. (Streaming guidance is community-consolidated; no single official page.)
  - https://www.devleader.ca/2026/06/30/httpclient-streaming-in-c-httpcompletionoption-readasstreamasync-and-serversent-events (community-reported)
- Uploads via Graph upload sessions: each byte range must be a multiple of **320 KiB (327,680 bytes)**, any single PUT under 60 MiB, 5-10 MiB recommended, resumable sessions for files over 10 MiB, no `Authorization` header on the fragment PUT. A 5-10 MiB chunk is always an LOH allocation, so a rented or single reusable buffer wrapped in `ReadOnlyMemoryContent` is mandatory. `createUploadSession` defaults `conflictBehavior` to **fail**; simple PUT defaults to **replace**: always set it explicitly.
  - https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0
- Buffer-light helpers new in .NET 11: `Utf8JsonWriter.Reset` (pool writers), `ReadOnlySequenceStream` over `ReadOnlySequence<byte>`, span-based Deflate/ZLib/GZip encoders.
  - https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries
- Bound everything (design rule 5): hydrations 3-4, uploads 2-3, delta ingestion 1; memory for transfers = chunk size x concurrency, fixed by construction.

---

## 6. Serialization

- System.Text.Json source generation only: one `partial class GraphJsonContext : JsonSerializerContext` with `[JsonSerializable(typeof(...))]` per root DTO, pass `GraphJsonContext.Default.DeltaPage` (a `JsonTypeInfo<T>`) to Serialize/Deserialize. `JsonSerializer.IsReflectionEnabledByDefault` is a link-time constant, so branching on it does not root the reflection resolver.
- **Metadata** generation mode is required for async/streaming (`JsonSourceGenerationMode.Serialization` fast-path "isn't supported for asynchronous serialization"). Set `[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]`.
- Large delta pages: `JsonSerializer.DeserializeAsync(responseStream, GraphJsonContext.Default.DeltaPage)` over the response stream; DTOs contain only the fields we read (id, name, size, eTag, cTag, file.hashes.quickXorHash, parentReference, folder.childCount, deleted, `@microsoft.graph.downloadUrl`, `@odata.nextLink`, `@odata.deltaLink`) to keep generated code and per-page allocations small. For 500k items the page stream is consumed and written to SQLite in transactions, never materialised as a whole.
- Members typed `object` need explicit `[JsonSerializable(typeof(bool))]` etc.; use `JsonStringEnumConverter<TEnum>`.
- .NET 11 additions: `GetTypeInfo<T>()` (throws) / `TryGetTypeInfo<T>()` (returns false); `[JsonInclude]` members may be non-public.
  - https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation
  - https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries

---

## 7. Idle discipline

Goal: when nothing happens, nothing runs. Idle Private Bytes must be independent of item count and the process must go quiet.

- No sub-minute timers. `System.Threading.Timer` callbacks run on pool threads and frequent timers keep threads alive and the process warm. Wake-ups are event-driven: cfapi callbacks, `ReadDirectoryChangesW`, network-change / power events, and delta polling with backoff (1 min active, back off to 15-60 min idle, daily safety full delta; socket.io push in P2).
  - https://learn.microsoft.com/en-us/dotnet/api/system.threading.timer?view=net-11.0
- No periodic `GC.Collect`, no periodic working-set trimming (section 3).
- No Hosting/Logging stack (file watchers, console logging infrastructure keep objects and threads alive). Settings via a small STJ source-generated JSON file; logs through a lightweight custom sink.
- Index on disk (SQLite via winsqlite3), small bounded LRU in memory. rclone's memory is dominated by its in-memory directory cache (community rule of thumb about 1 GB per 1M objects; v1.70+ spills listings to disk above `--list-cutoff`) and per-transfer buffers; this is the pattern NoDrive avoids by construction.
  - https://rclone.org/faq/
  - https://forum.rclone.org/t/how-to-reduce-rclone-memory-usage/44082 (community-reported)
- Short `PooledConnectionIdleTimeout` so sockets close after a cycle (section 5).
- After each burst: dispose streams, return pooled buffers, drop caches, Aggressive collect (1-3), verify `TotalCommittedBytes`.
- Threads: with the Windows thread pool the runtime does not retire threads on our schedule; keep the worker set small and fixed (bounded channels + a few long-lived workers) so idle thread count is predictable. Measure in P0.

---

## 8. Published measurements and the spike measurement plan

### What exists (order-of-magnitude only, all ASP.NET-style, no tray/cfapi data)
Skeptic verdict: unverifiable secondary blog measurements, single samples, different metrics (WorkingSet64 vs VmRSS vs PowerBI max working set). Microsoft's docs only say Native AOT apps "have faster startup time and smaller memory footprints".

| Source | Setup | AOT | JIT | Notes |
|---|---|---|---|---|
| Thinktecture (community) | .NET 8 empty web app, Windows x64, WorkingSet64 after StartAsync | ~23 MB | ~40 MB | Linux x64 19-21 MB vs >50 MB |
| startdebugging.net (community, May 2026) | .NET 11 RC2 minimal API, Ubuntu 24.04, VmRSS after 10 s | 18 MB | 41 MB (R2R 39 MB) | single figures, no variance |
| bitmiracle quoting Microsoft ASP.NET benchmarks (community) | .NET 9 RC, Windows Stage2 max working set | 152 MB | 167 MB | "On Windows, the difference is smaller" for large apps |
| dev.to (community) | .NET 10 hello-world binary size on Windows 11 x64 | ~1.06 MB (0.73 MB size-tuned) | - | size only, no memory data |

- https://www.thinktecture.com/en/?p=20991
- https://startdebugging.net/2026/05/native-aot-vs-readytorun-vs-jit-in-dotnet-11/
- https://bitmiracle.com/pdf-library/howto/native-aot-performance
- https://dev.to/nazecode/how-big-is-hello-world-in-c-from-76-mb-down-to-073-mb-ja
- https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/
- Disregard: C# Corner "Benchmarking .NET 11 NativeAOT CLI startup and memory usage" (2026-08-14) contains no measured numbers.

No primary source measures an idle Windows Native AOT tray/console process, OneDrive.exe idle memory (only troubleshooting articles; a competing client's forum treats ~114 MB idle as acceptable), or an rclone mount (community: 100-500 MB on Windows depending on transfers, memory often not returned when idle). Expect a trivial idle AOT process to land well under the 23 MB web-app figure, but measure.
- https://forums.insynchq.com/t/memory-usage-on-the-high/2818 (community-reported)
- https://forum.rclone.org/t/rclone-how-to-limit-ram-usage/43728 (community-reported)

### Measurement plan (P0 step 2, week one, before architecture is locked)
1. Publish three spikes on the Windows 11 x64 PC, each as Native AOT and as framework-dependent JIT: (a) hello-world WinExe with message loop, (b) tray-only (message-only window + Shell_NotifyIcon), (c) tray + HttpClient doing a 1 GB streamed download with pooled buffers.
2. Record at idle after 2 min and 10 min, and after the download: `Process.PrivateMemorySize64`, `GC.GetGCMemoryInfo().TotalCommittedBytes` and `HeapSizeBytes`, Working Set, thread count, handle count. Private Bytes / Commit is the number that matters.
3. Repeat with `UseWindowsThreadPool=false` to compare idle thread count (section 4).
4. Tooling: dotnet-counters / PerfView for the JIT variant; EventPipe (`DOTNET_EnableEventPipe`) or Windows Performance Recorder for the AOT variant.
5. Baselines on the same machine: OneDrive.exe idle (signed in, synced) and `rclone mount` against a OneDrive remote.
6. Then set `ConserveMemory`, `RegionSize`, `HeapHardLimit` from the measured peak rather than defaults, and check the burst-release pattern (1 vs 2 vs 3 Aggressive collects to drop commit).
7. Feed the acceptance budget: idle under 30 MB Private Bytes with a 500k-item index (P0 step 6 synthetic drive); commit back to within 10 MB of idle within 60 s after a 10 GB hydration burst; flat peak during 500k-item delta ingestion.

---

## 9. UI framework options for the settings process

Both candidates are acceptable only because they run in an on-demand process that exits; neither goes in the engine. v1 may need no window at all (tray menu + browser sign-in + JSON config).

### WinUI 3 (Windows App SDK)
- `PublishAot` supported since WASDK 1.6 (Sept 2024) with CsWinRT 2.1.1+; developer must root reflection-based `{Binding}` targets, mark classes `partial` for the CsWinRT AOT analyzers; collection expressions cannot cross the WinRT ABI; WebView2 projections were not AOT-compatible at 1.6 (later status unverified). The post-navigation GC-thread hang was fixed as of .NET 9 RC2.
- WASDK 2.x: 2.0.1 GA 2026-04-29, latest **2.6.5** (2026-11-02), 2.6.6-Experimental 2026-11-06; release notes contain no Native AOT mentions and still carry memory-leak fixes (2.1.3 ItemsRepeater, 2.5.4-Experimental resource tracking). Press reports (WindowsLatest, PCWorld, 2026) of Microsoft acknowledging WinUI RAM usage are secondary sources.
- Runtime dependency on the Windows App SDK runtime; unpackaged apps need the bootstrapper.
  - https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-6
  - https://raw.githubusercontent.com/microsoft/CsWinRT/master/docs/aot-trimming.md
  - https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0
  - https://windowslatest.com/2026/07/27/microsoft-admits-windows-11-native-apps-hog-ram-promises-a-winui-performance-boost-before-start-menu-rewrite (community/press)

### Avalonia 12
- Officially supports Native AOT; stable **12.1.6** (2026-11-09, MIT, net8.0/net10.0 TFMs). Requirements: `PublishAot` on the main project, `IsAotCompatible=true` on all projects, `x:CompileBindings="True"`, `BuiltInComInteropSupport=false` only needed before 12.0, `TrimmerRootAssembly` only to fix reflection errors. Limitations: dynamic control creation must be configured in trimmer settings; some third-party controls not AOT-compatible. Ships SkiaSharp natives (heavier on disk than a Win32 loop; RID-agnostic packages drag ~190 MB of natives, package size not RAM).
- Community template claims ~18 MB binary / ~20 MB memory on Windows (.NET 10): unverified, no methodology.
  - https://docs.avaloniaui.net/docs/deployment/native-aot
  - https://www.nuget.org/packages/Avalonia
  - https://github.com/lixinyang123/AvaloniaAOT (community-reported)
- Uno Platform 6.6 (Aug 2026) also added Native AOT on Windows (not evaluated further).
  - https://infoq.com/news/2026/08/uno-platform-6-6

### Decision input
Idle memory of a blank AOT WinUI 3 app vs a blank AOT Avalonia 12 app is **unmeasured**; settle by local measurement when the settings window is built (P2). Because the process exits when closed, the engine's idle budget is unaffected either way; the comparison is about cold-start time and disk size.

---

## 10. Related critique gaps carried into this topic

- **Metadata store** (gap resolved partly): Microsoft.Data.Sqlite.Core + `SQLitePCLRaw.provider.winsqlite3` (system `winsqlite3.dll`, zero native payload) vs raw `LibraryImport` vs custom binary index; AOT status of Microsoft.Data.Sqlite unofficial. Verify `PublishAot` warnings in P0 step 5. Design consequence for memory: the index is the only structure proportional to item count and it lives on disk.
- **Build host** (gap resolved): physical Windows 11 x64 PC; no cross-OS AOT from macOS.
- **Settings UI** (decision for user, deferred): WinUI 3 vs Avalonia 12 vs no GUI in v1; v1 plan is tray + browser sign-in + JSON config.
- **Upload buffer sizing** (missing point): 320 KiB alignment, under 60 MiB per PUT, 5-10 MiB chunks => LOH, pooled buffers mandatory (section 5).
- **Hosting** (missing point): AOT status unverified, avoided anyway.

---

## 11. Open questions (to resolve in P0 unless noted)

1. Actual idle Private Bytes / commit / thread count of a win-x64 Native AOT process hosting a message-only window, cfapi connection, SocketsHttpHandler and SQLite (no published data).
2. Quantified effect of `ConserveMemory` 5-7, `RegionSize` 1 MB and a small `HeapHardLimit` on a tiny workstation heap (knobs documented, no numbers; Maoni's regions-vs-segments article returned HTTP 403 and was not read).
3. Does the .NET 10 UOH age-based decommit (3 collects) apply to workstation GC? Verify with `TotalCommittedBytes` after a 10 GB burst.
4. Windows thread pool vs portable pool (`UseWindowsThreadPool=false`): which yields fewer resident threads and less commit at idle, and does either affect cfapi callback latency?
5. Do .NET 10/11 JIT escape-analysis / stack-allocation improvements apply to ILC (Native AOT) code generation? ILC uses RyuJIT so likely, but what's-new pages describe them as JIT features.
6. Semantics and safe values of the private `DOTNET_GCgen0size` for a small heap (probably leave alone).
7. Runtime Async stability in Native AOT on an STS release; BCL already uses it, our code opts out in v1.
8. Microsoft.Data.Sqlite + winsqlite3 under `PublishAot` on net11.0: warnings, `Batteries.Init` behaviour (P0 step 5).
9. Idle memory and cold start of blank AOT WinUI 3 (WASDK 2.6.5) vs Avalonia 12.1.6 (P2, local measurement).
10. OneDrive.exe and `rclone mount` idle baselines on the user's PC for the comparison the project wants to publish (P0 step 2).
11. CsWinRT projection (`net11.0-windows10.0.26100.0`) under Native AOT on .NET 11: CsWinRT 2.3.1 documents .NET 9/10 only (cfapi topic owns this; it decides the registration fallback).
12. Whether `StackTraceLineNumberSupport` is worth its size cost for field diagnostics of hangs (decide after first AOT size measurement).
