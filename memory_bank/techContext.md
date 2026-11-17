# Tech Context

Tech stack, development setup, build commands, constraints and reference material for NoDrive. Versions are as of 2026-11-13. Every version, limit and bug claim carries a source URL from the 2026-11-13 research pass, or is marked "community-reported" (Microsoft Q&A answers from external vendor moderators, forum threads) or "to verify in spike". See `memory_bank/decisions/` for the reasoning behind each choice.

## 0. Current build facts (2026-11-16)

- **Stack as built**: .NET 11 SDK (`global.json`), `NoDrive.Engine` net11.0-windows10.0.26100.0 Native AOT (about 8 MB exe), CsWin32 for Cf*/tray/shell P/Invoke (`NativeMethods.txt`), WinRT `StorageProviderSyncRootManager` for registration, `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.provider.winsqlite3` (system SQLite), `System.Text.Json` source generation, `HttpListener` (sign-in loopback and the local settings page), no Graph SDK, no MSAL, no UI framework. Invariant globalization is on, so locale and UI language come from Win32 (`GetUserDefaultLocaleName`, `GetUserDefaultUILanguage`).
- **Installer**: WiX Toolset **5.0.2** (pinned; v6+ carries the Open Source Maintenance Fee EULA) with `WixToolset.UI.wixext`; `installer/NoDrive.wxs`, `installer/license.rtf`; built by the `windows` workflow (continue-on-error step; the log is published too).
- **Tools**: `tools/make_icon.py` (pure Python, generates `src/NoDrive.Engine/Assets/nodrive.ico`, embedded via `ApplicationIcon`); `docs/` guides (EN/ES, linked from the settings page to GitHub blob URLs).
- **CI outputs**: artifacts `engine-win-x64`, `NoDrive-installer-win-x64`, `spikes-win-x64`; branch `ci-binaries` with `engine/`, `installer/`, `BUILD.txt` (commit). The dev flow copies the AOT exe or the MSI to the PC from that branch.
- **Local web page**: `http://localhost:47653` (config `webPort`), per-run token, see ADR-0011. **Config fields** (`config.json`): `clientId`, `syncRoot`, `hydrationWorkers`, `populationBudget`, `pollActiveSeconds`, `pollIdleMaxSeconds`, `language`, `downloadKiBps`, `uploadKiBps`, `hydrationPolicy`, `webPort`.
- **Engine commands**: `--status`, `--cleanup [--delete-root]`, `--quit [--wait N]`, `--pending` (exit 3 when queued work), `--uninstall [--force] [--delete-data] [--delete-root]`, `--sync-root`, `--data-dir`.
- **Dev gotcha**: a locally built, unsigned `NoDrive.Engine.dll` (JIT) can be blocked by Windows Application Control (0x800711C7) on the test PC; test the CI AOT exe or MSI instead. Native AOT ignores `runtimeconfig.template.json` (settings live in `eng/LowFootprint.props`).
- **Tests**: 48 xunit.v3 tests on Microsoft.Testing.Platform (`dotnet test NoDrive.slnx`), including index, delta, population, hydration policy, read-ahead, Graph with a fake handler, upload and ops journals, bandwidth limiter.

## 1. Tech stack

| Component | Choice | Version (2026-11-13) | Licence | AOT status | Source |
|---|---|---|---|---|---|
| Runtime / SDK | .NET 11 (STS, GA 2026-11-10, support ends Nov 2028, same end date as .NET 10 LTS) | SDK 11.0.100 (verified with `dotnet --version` on 2026-11-13 on the macOS planning box; install the same on the Windows PC) | MIT | Native AOT is an opt-in publish option; .NET 11 ships a Native AOT `dotnet` CLI host by default | https://devblogs.microsoft.com/dotnet/announcing-dotnet-11/ , https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core |
| Compilation | Native AOT (`PublishAot=true`), win-x64 primary | - | - | Requires VS 2022+ "Desktop development with C++" on the build machine; no cross-OS compile | https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ , https://learn.microsoft.com/dotnet/core/deploying/native-aot/cross-compile |
| Win32 interop (cfapi, shell, tray) | Microsoft.Windows.CsWin32 | 0.3.371 (2026-11-10), needs Win32Metadata >= 71.0.45-preview | MIT | AOT-safe with `CsWin32RunAsBuildTask=true`, `DisableRuntimeMarshalling=true`, `"allowMarshaling": false`; `AllowUnsafeBlocks` required. All 36 `Cf*` functions are in `Windows.Win32.Storage.CloudFilters`. No published end-to-end sample of cfapi callbacks under AOT: verify in spike | https://www.nuget.org/packages/Microsoft.Windows.CsWin32 , https://microsoft.github.io/CsWin32/docs/getting-started.html , https://microsoft.github.io/windows-docs-rs/doc/windows/Win32/Storage/CloudFilters/index.html |
| WinRT interop (`Windows.Storage.Provider`) | Windows SDK projection via TFM `net11.0-windows10.0.26100.0` (CsWinRT) | CsWinRT 2.3.1 (2026-07-22); 3.0.0-preview exists | MIT (to verify) | AOT-compatible since CsWinRT 2.1.1 / projection build .38, but documented for .NET 9/10 only; .NET 11 unverified. NETSDK1229 warns that a TFM revision of `1` pulls CsWinRT 3.0 preview | https://github.com/microsoft/CsWinRT/blob/master/docs/aot-trimming.md , https://www.nuget.org/packages/Microsoft.Windows.CsWinRT , https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-enhance , https://github.com/dotnet/sdk/discussions/54975 |
| Serialization | System.Text.Json source generators (one `JsonSerializerContext`, Metadata mode for streaming delta pages) | in-box | MIT | AOT-safe by design; reflection serializer is disabled under trimming | https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation |
| Index / journal | SQLite via the system `winsqlite3.dll` (Microsoft.Data.Sqlite.Core + SQLitePCLRaw.provider.winsqlite3, or raw `LibraryImport` of ~15 functions) | Microsoft.Data.Sqlite.Core: current stable (to verify) | MIT (Microsoft.Data.Sqlite, to verify); Apache-2.0 (SQLitePCLRaw, to verify); SQLite itself public domain | Microsoft.Data.Sqlite AOT status not officially stated; trim warnings closed not-planned (efcore #29725). Verify `PublishAot` output in spike; fallback raw P/Invoke | https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions , https://github.com/dotnet/efcore/issues/29725 |
| Auth | Hand-rolled OAuth2 authorization-code + PKCE (S256), system browser, loopback `HttpListener`, DPAPI (`ProtectedData`) token store | - | - | Zero NuGet dependencies, AOT-clean by construction | https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow , https://learn.microsoft.com/en-us/entra/identity-platform/refresh-tokens |
| Graph client | Hand-written `HttpClient` + STJ source-gen DTOs (~10 endpoints). No Microsoft.Graph SDK | - | - | Microsoft.Graph 6.10.0 is 52.6 MB, `IsTrimmable` but not `IsAotCompatible`, open trimming bug #2944 | https://www.nuget.org/packages/Microsoft.Graph , https://github.com/microsoftgraph/msgraph-sdk-dotnet/issues/2944 |
| Not in v1: MSAL / WAM broker | Microsoft.Identity.Client + .Broker | 4.92.0 (2026-10-29); NativeInterop 0.20.7 | MIT | Core is `IsAotCompatible` since 4.77.1; Broker ships netstandard2.0/net462 only (not analyzed), 21.65 MB nupkg, msalruntime.dll cannot be bundled single-file, open AOT issue #6232 | https://www.nuget.org/packages/Microsoft.Identity.Client , https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/issues/6232 |
| Optional later: settings UI | Avalonia 12 or WinUI 3 (Windows App SDK) in a separate on-demand process | Avalonia 12.1.6 (2026-11-09); Windows App SDK 2.6.5 (2026-11-02) | Avalonia MIT; Windows App SDK MIT (to verify) | Avalonia officially supports Native AOT (compiled bindings, `IsAotCompatible`); WinUI 3 AOT since Windows App SDK 1.6 with CsWinRT 2.1.1+ and manual rooting of `{Binding}` targets. Idle memory of either is unmeasured | https://docs.avaloniaui.net/docs/deployment/native-aot , https://www.nuget.org/packages/Avalonia , https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-6 |
| Optional later: toasts | Windows App SDK `AppNotificationManager` (works unpackaged) | 2.6.5 | MIT (to verify) | Adds the Windows App SDK runtime dependency; cfapi already raises hydration toasts itself, so only if needed | https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet |
| Not used | WinForms, WPF (not trim/AOT compatible); Microsoft.Extensions.Hosting (AOT status unverified, ~16 packages); Vanara.PInvoke.CldApi 5.0.8 (MIT, 2026-10-24, classic marshalling, no AOT claim) only for throwaway prototyping | - | - | - | https://api.github.com/repos/dotnet/wpf/issues/3811 , https://www.nuget.org/packages/Vanara.PInvoke.CldApi |

Licences marked "to verify" were not checked in the research pass; confirm before writing `THIRD-PARTY-NOTICES.md` (ADR-0006).

Decision records behind this table: [ADR-0001](decisions/ADR-0001.md) cfapi over a custom client, [ADR-0002](decisions/ADR-0002.md) .NET 11 + Native AOT with fallbacks, [ADR-0003](decisions/ADR-0003.md) hand-rolled PKCE auth, [ADR-0004](decisions/ADR-0004.md) no Microsoft.Graph SDK, [ADR-0005](decisions/ADR-0005.md) SQLite via winsqlite3, [ADR-0006](decisions/ADR-0006.md) MIT licence, [ADR-0007](decisions/ADR-0007.md) lazy population + PROGRESSIVE hydration for 500k items.

## 2. Development setup

### Windows 11 x64 PC (build, AOT publish, Explorer testing)

- Physical Windows 11 x64 machine. win-x64 is the primary RID; win-arm64 is a secondary target built in CI if ever needed.
- Minimum OS: Windows 11 24H2 with the August 2025 cumulative update (KB5063878, `cldflt.sys` 10.0.26100.4946) or later. That update fixed CVE-2025-50170, an EoP in the placeholder-creation path (`HsmpOpCreatePlaceholders`, reached via `CfCreatePlaceholders`), which is exactly the call NoDrive makes in bulk; `cldflt.sys` behaviour changed in that patch and in later 2025 fixes (CVE-2025-55680 in KB5066835), so pin the minimum CU rather than "24H2". Source: https://starlabs.sg/advisories/25/25-50170/ (advisory); patch note in the research is secondary.
- Test matrix: fully patched 24H2 and 25H2, Defender on, files > 100 MB, apps that memory-map files (Photos, Office).
- Install: .NET 11 SDK; Visual Studio 2022 or later (or Build Tools) with the "Desktop development with C++" workload, required by Native AOT publish. Source: https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/
- Hardware baseline: .NET 11 requires x86-64-v2; fails to start on pre-2013 CPUs. Source: https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime
- Enable the `Microsoft-Windows-CloudFiles/Operational` event log channel (see Diagnostics).
- OneDrive.exe can stay installed and running (coexistence, ADR-0013); NoDrive just must not register its root inside a folder another provider owns. Two providers cannot nest or overlap sync roots. Source: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfregistersyncroot

### Toolchain facts verified while scaffolding (2026-11-13, macOS, .NET SDK 11.0.100)

- **Native AOT ignores `runtimeconfig.template.json`** (CI run #5, ILCompiler 11.0.0: "The published project has a runtimeconfig.template.json that is not supported by PublishAot. Move the configuration to the project file using RuntimeHostConfigurationOption"). All GC/runtime knobs therefore live as `<RuntimeHostConfigurationOption Include="System.GC.ConserveMemory" Value="6" />` items in `eng/LowFootprint.props`, imported by the engine and the AOT spikes. Verify in a published AOT binary with `GC.GetConfigurationVariables()` (prints the effective GC settings) before trusting any memory measurement.

- The solution file is `NoDrive.slnx` (XML solution format), hand-authored. `dotnet sln add` in SDK 11.0.100 refused to add a second project into the same solution folder ("folder src already contains a project named NoDrive.Core.csproj"); editing the `.slnx` by hand is the workaround.
- `dotnet test` on SDK 10+ needs the Microsoft.Testing.Platform opt-in. In SDK 11 it lives in `global.json` (`"test": { "runner": "Microsoft.Testing.Platform" }`), not in `dotnet.config`; the test project sets `UseMicrosoftTestingPlatformRunner=true` (in `tests/Directory.Build.props`). xunit.v3 4.0.3 runs natively on MTP; `Microsoft.NET.Test.Sdk` is not referenced. Verified: `dotnet test NoDrive.slnx` runs 2/2 tests green.
- CsWin32 0.3.371 with `allowMarshaling=false` fails on `Shell_NotifyIcon` for an AnyCPU build because `NOTIFYICONDATAW` is architecture-specific. The engine csproj sets `<CsWin32PlatformTarget>x64</CsWin32PlatformTarget>` when no RuntimeIdentifier is given; a `-r win-x64` / `-r win-arm64` publish sets PlatformTarget itself.
- With `EnableWindowsTargeting=true` (root `Directory.Build.props`) the `net11.0-windows10.0.26100.0` engine project restores (including the Windows SDK projection) and compiles on macOS with 0 warnings and `TreatWarningsAsErrors`. It cannot run or AOT-publish there.
- Central package management (`Directory.Packages.props`) pins: Microsoft.Windows.CsWin32 0.3.371, Microsoft.Data.Sqlite.Core 11.0.0, SQLitePCLRaw.provider.winsqlite3 3.0.5, xunit.v3 4.0.3, xunit.runner.visualstudio 4.0.1 (latest stable on NuGet 2026-11-13).
- `TreatWarningsAsErrors` + `AnalysisLevel latest-recommended` are on for every project; CA1707 (underscores) is suppressed for test projects only.

### CI binaries branch

- Every push to `main` makes the `windows` workflow force-push the built Native AOT binaries (engine, spikes, publish logs, `BUILD.txt` with the commit sha) to the orphan branch `ci-binaries` (job permission `contents: write`). Fetch with `git fetch origin ci-binaries` and `git show origin/ci-binaries:<path>`; used to ship AOT builds to the Windows test PC without a browser download or C++ build tools.

### Repository

- Remote: private GitHub repo `https://github.com/andresvettori/nodrive` (SSH `git@github.com:andresvettori/nodrive.git`), branch `main`, first push 2026-11-13. CI: `.github/workflows/windows.yml` on windows-latest.

### Repository layout (P0, from the approved plan)

- `CLAUDE.md`: memory-bank reading/updating rules plus build commands (read all `memory_bank/` files at the start of every task; update `activeContext.md` and `progress.md` after each session; review every file on "update memory bank").
- `memory_bank/`: this documentation (core files, `research/`, `decisions/` with the ADRs; no separate docs folder).
- `NoDrive.slnx`
- `src/NoDrive.Engine/`: WinExe, `net11.0-windows10.0.26100.0`, `PublishAot`, CsWin32 `NativeMethods.txt` (Cf*, `Shell_NotifyIconW`, window/message-loop functions), GC settings via `RuntimeHostConfigurationOption` in `eng/LowFootprint.props` (sections 4-5).
- `src/NoDrive.Graph/`: auth (PKCE), DTOs + `JsonSerializerContext`, delta, download, upload (simple and session), create folder, delete, move. quickXorHash is not implemented.
- `src/NoDrive.Core/`: reconciler, index, state machine, abstractions `IRemoteDrive` / `IPlaceholderStore` (testable on macOS).
- `tests/NoDrive.Core.Tests/`: unit + property tests against the fake Graph server.
- `installer/` (WiX MSI), `docs/` (guides EN/ES), `tools/` (icon generator).
- `spikes/`: the P0 measurement apps (throwaway; only the fake server and latency histogram graduate to `tests/`).
- `.github/workflows/windows.yml`: win-x64 AOT publish + tests on `windows-latest` (optional).

### macOS (planning, scaffolding, Core unit tests)

- The .NET 11 SDK on macOS can restore and build Windows TFM projects with `-p:EnableWindowsTargeting=true` (standard SDK property; not covered by the research, verify the first time). It cannot run `NoDrive.Engine`, cannot exercise cfapi, and cannot AOT-publish for Windows: "Native AOT does not support cross-OS compilation". Source: https://learn.microsoft.com/dotnet/core/deploying/native-aot/cross-compile
- `NoDrive.Core` (reconciler, index, state machine) targets plain `net11.0` behind the `IRemoteDrive` / `IPlaceholderStore` seams and its tests run on macOS against the fake Graph server.
- Unofficial Zig-linker cross-compile packages exist (StuDev.AotAnywhere, Fallenwood.PublishAotCross.Extensions) but are unsupported; do not depend on them.

### Optional CI: GitHub Actions `windows-latest`

- `dotnet test` for all test projects and `dotnet publish -r win-x64 -c Release -p:PublishAot=true` for `NoDrive.Engine`; fail the job on any IL2xxx/IL3xxx warning from our own code.
- Workflow file: `.github/workflows/windows.yml` (owned by the repo scaffolding, not by this document).

## 3. Build and publish commands

```powershell
# restore + build everything (Windows)
dotnet build NoDrive.slnx -c Debug

# unit + property tests (Windows or macOS; macOS needs the Windows-targeting switch for the solution)
dotnet test NoDrive.slnx -c Debug
dotnet test NoDrive.slnx -c Debug -p:EnableWindowsTargeting=true   # macOS

# Native AOT publish of the engine (Windows only)
dotnet publish src/NoDrive.Engine/NoDrive.Engine.csproj -r win-x64 -c Release -p:PublishAot=true

# optional .NET 11 opt-in: skip the full build chain on AOT publish
dotnet publish src/NoDrive.Engine/NoDrive.Engine.csproj -r win-x64 -c Release -p:PublishAot=true -p:UseOptimizedPublish=true
```

- `UseOptimizedPublish` defaults to false for AOT projects in .NET 11. Source: https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/sdk
- Publish output includes a separate `.pdb`; keep it with every shipped build for crash-dump symbolication.
- Framework-dependent trimmed JIT fallback (kill-criteria path, see ADR-0002): `dotnet publish -r win-x64 -c Release -p:PublishAot=false -p:PublishTrimmed=true -p:PublishSingleFile=true`.

## 4. Recommended csproj properties for `NoDrive.Engine`

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net11.0-windows10.0.26100.0</TargetFramework>
  <SupportedOSPlatformVersion>10.0.26100.0</SupportedOSPlatformVersion> <!-- Windows 11 24H2 floor; see section 2 -->
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>

  <!-- Native AOT -->
  <PublishAot>true</PublishAot>
  <OptimizationPreference>Size</OptimizationPreference>
  <IsAotCompatible>true</IsAotCompatible>

  <!-- Trimming feature switches (binary size and mapped code; idle heap is governed by GC settings) -->
  <InvariantGlobalization>true</InvariantGlobalization>
  <UseSystemResourceKeys>true</UseSystemResourceKeys>
  <EventSourceSupport>false</EventSourceSupport>
  <MetricsSupport>false</MetricsSupport>
  <HttpActivityPropagationSupport>false</HttpActivityPropagationSupport>
  <Http3Support>false</Http3Support>
  <XmlResolverIsNetworkingEnabledByDefault>false</XmlResolverIsNetworkingEnabledByDefault>
  <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
  <!-- keep DebuggerSupport=true: setting it false also strips symbols (TrimmerRemoveSymbols) and kills symbolicated dumps -->
  <DebuggerSupport>true</DebuggerSupport>
  <!-- keep StackTraceSupport=true in v1: false now throws NotSupportedException from APIs needing stack data in .NET 11 -->
  <TrimmerSingleWarn>false</TrimmerSingleWarn>

  <!-- Interop (CsWin32 + cfapi function-pointer callbacks) -->
  <DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  <CsWin32RunAsBuildTask>true</CsWin32RunAsBuildTask>

  <!-- WinRT projection AOT optimizer (CsWinRT) -->
  <CsWinRTAotOptimizerEnabled>Auto</CsWinRTAotOptimizerEnabled>

  <!-- Runtime Async is preview in .NET 11; the BCL itself is compiled with it on and that cannot be disabled -->
  <UseRuntimeAsync>false</UseRuntimeAsync>
</PropertyGroup>
```

`NativeMethods.json`: `{ "allowMarshaling": false, "useSafeHandles": true }`. `NativeMethods.txt` lists only the functions used (`CfRegisterSyncRoot`, `CfUnregisterSyncRoot`, `CfConnectSyncRoot`, `CfDisconnectSyncRoot`, `CfCreatePlaceholders`, `CfConvertToPlaceholder`, `CfUpdatePlaceholder`, `CfExecute`, `CfReportProviderProgress`, `CfSetInSyncState`, `CfSetPinState`, `CfHydratePlaceholder`, `CfDehydratePlaceholder`, `CfGetPlaceholderInfo`, `CfOpenFileWithOplock`, `CfCloseHandle`, `CfGetPlatformInfo`, `CfGetSyncRootInfoByPath`, plus `Shell_NotifyIconW`, `RegisterClassExW`, `CreateWindowExW`, `DefWindowProcW`, `GetMessageW`, `TranslateMessage`, `DispatchMessageW`, `RegisterWindowMessageW`, `CreatePopupMenu`, `TrackPopupMenuEx`, `LoadImageW`, `ReadDirectoryChangesW`). Callbacks are `[UnmanagedCallersOnly]` static methods (`CF_CALLBACK` is a C function pointer).

Sources: https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/optimizing , https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trimming-options , https://microsoft.github.io/CsWin32/docs/getting-started.html , https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime

Notes:
- Shell COM servers (thumbnails, status flyout, P2) must use `[GeneratedComInterface]`/`[GeneratedComClass]` (ComWrappers source generation); Native AOT has no built-in COM on Windows. Source: https://learn.microsoft.com/en-us/dotnet/standard/native-interop/comwrappers-source-generation
- `VerifyReferenceAotCompatibility=true` emits IL3058 for every package built before .NET 10 regardless of real behaviour (the `IsAotCompatible` metadata was introduced in .NET 10); use it as inventory, not as a gate. Source: https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/
- `ShouldEvictConnection` on `SocketsHttpHandler` is `[Experimental("SYSLIB5008")]` in .NET 11; prefer `PooledConnectionIdleTimeout` 20-30 s and `PooledConnectionLifetime` ~5 min. Source: https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.shouldevictconnection?view=net-11.0

## 5. runtimeconfig GC settings (engine)

Set via `runtimeconfig.template.json` or `<ItemGroup><RuntimeHostConfigurationOption Include="..." Value="..." /></ItemGroup>`. runtimeconfig values are decimal; the `DOTNET_GC*` environment variables are hex.

```json
{
  "configProperties": {
    "System.GC.Server": false,
    "System.GC.Concurrent": false,
    "System.GC.ConserveMemory": 6,
    "System.GC.RegionSize": 1048576,
    "System.GC.HeapHardLimit": 134217728,
    "System.GC.RetainVM": false
  }
}
```

- Workstation GC, `Concurrent=false` (also makes `GCLatencyMode.Batch` the default). Do not use `SustainedLowLatency` ("results in a larger managed heap size"). DATAS applies to Server GC only. Source: https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector , https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/latency
- `ConserveMemory` 1-9, "Start with a value between 5 and 7"; non-zero also auto-compacts the LOH. Tune in the spike.
- `RegionSize`: "For processes that have very small GC heaps ... The recommendation is 1 MB" (default 4 MB). Runtimeconfig name available since .NET 10.
- `HeapHardLimit` (64-bit only): optional, 64-128 MB; a bug then fails loudly (OOM) instead of eating the machine; reserve becomes 5x the limit. No MSBuild property, use `RuntimeHostConfigurationOption`.
- After a transfer burst: `GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive)` (decommits as much as possible; implies compacting). .NET 10+ ages UOH free regions over two gen2 GCs, so a second Aggressive collect a few seconds later may be needed; verify with `GC.GetGCMemoryInfo().TotalCommittedBytes`. Never run periodic collects while idle; `EmptyWorkingSet`/`SetProcessWorkingSetSize` only moves pages to standby and does not reduce commit. Sources: https://learn.microsoft.com/en-us/dotnet/api/system.gc.collect?view=net-11.0 , https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-emptyworkingset
- Thread pool: AOT apps on Windows use the Windows thread pool by default (`SetMinThreads`/`SetMaxThreads` are no-ops); the portable pool (`UseWindowsThreadPool=false`) retires idle threads after 20 s. Measure idle thread count under both in the spike. Default thread stack 1.5 MB, configurable via `System.Threading.DefaultStackSize` (.NET 10+). Source: https://learn.microsoft.com/en-us/dotnet/core/runtime-config/threading
- Measure Private Bytes / Commit (`Process.PrivateMemorySize64`, `GC.GetGCMemoryInfo`), not Task Manager "Memory" alone.

## 6. Entra app registration prerequisites

- A personal Microsoft account alone cannot create app registrations (it lands in the directory-less "Microsoft Services" tenant). Create a free Entra tenant first (free Azure sign-up). Community-reported (Microsoft Q&A moderators): https://learn.microsoft.com/en-us/answers/questions/5693812/app-registration-option-disabled-for-my-account-in
- Registration: platform "Mobile and desktop applications"; `signInAudience` = `AzureADandPersonalMicrosoftAccount`; "Allow public client flows" = yes; redirect URI `http://localhost` (port component is ignored for loopback; IPv6 `[::1]` unsupported; do not register several localhost URIs differing only by port). Source: https://learn.microsoft.com/en-us/entra/identity-platform/reply-url , https://learn.microsoft.com/en-us/entra/identity-platform/supported-accounts-validation
- Scopes: `Files.ReadWrite offline_access` (optionally `openid User.Read` for a display name). `Files.Read.All` only if a future "shared with me" feature is added. Source: https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/permissions_reference
- Authority: `https://login.microsoftonline.com/consumers/oauth2/v2.0/{authorize,token}` for MSA only (`/common` also admits work accounts). No client secret (public client). Authorization codes expire in ~1 minute; every refresh returns a new refresh token, discard the old one. Source: https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow , https://learn.microsoft.com/en-us/entra/identity-platform/refresh-tokens
- Day-one check: newly consented personal-account apps have been reported (Aug-Oct 2026, no Microsoft reply) to get `403 accessDenied / serviceReadOnly` or `503 itemDisabledDueToPendingProvisioning` on all drive endpoints. Test a fresh app + fresh account with `Files.ReadWrite` (not AppFolder-only) before anything else. Community-reported: https://learn.microsoft.com/en-us/answers/questions/5983388/personal-onedrive-newly-consented-apps-get-403-acc
- Consent screen shows "unverified publisher"; fine for self-use.
- User-Agent decoration for throttling fairness: `ISV|<You>|NoDrive/<version>`. Source: https://learn.microsoft.com/en-us/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online

### App registration on record

- **Client ID `6711e7c6-ec09-42df-a5bb-bfb449119fdc`**, created by the user on 2026-11-13 in their own Entra tenant; embedded as `GraphEndpoints.DefaultClientId` in `src/NoDrive.Graph`. Not a secret. Still to verify on the registration (P0 step 1): supported account types = AzureADandPersonalMicrosoftAccount, platform Mobile and desktop with redirect `http://localhost`, Allow public client flows = Yes, delegated permissions `Files.ReadWrite` + `offline_access` only, no client secret created. First sign-in with the personal account is the 403 `serviceReadOnly` check.

## 7. Technical constraints

### cfapi (platform)

- NTFS only; `cldflt.sys` minifilter; placeholders are reparse points of ~1 KB. Never place the sync root at a drive root (community-reported via Nextcloud PR #3290 / Microsoft Q&A). Not ReFS / Dev Drive. Source: https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine
- Provider is a normal per-user, non-elevated desktop process in the interactive session. Not a Windows service: session-0 hydration fails with `STATUS_CLOUD_FILE_ACCESS_DENIED`. Community-reported (reproduced by a Microsoft Q&A responder in 2022; GitHub issue #245 has no Microsoft reply): https://learn.microsoft.com/en-us/answers/questions/892349/cfapi-allow-data-fetch-for-services , https://github.com/microsoft/Windows-classic-samples/issues/245
- Provider must stay running: dehydrated placeholders fail with `ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING` (0x8007016A) otherwise (error code community-reported: https://github.com/B3nedikt/Tenshu/pull/6 ). Exactly one `CfConnectSyncRoot` connection per sync root. Source: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfconnectsyncroot
- Every callback has a fixed 60 s deadline; `CfReportProviderProgress` resets it. Callbacks arrive on arbitrary thread-pool threads. The 60 s figure is documented in the `CF_CALLBACK_TYPE` remarks ("Every callback request has a fixed 60 second timeout"): https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ne-cfapi-cf_callback_type , https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfreportproviderprogress . That it is not configurable and that 0x800701AA means an unanswered callback is community-reported (Microsoft Q&A FAQ, vendor moderator: https://learn.microsoft.com/en-us/answers/questions/2288103/cloud-file-api-faq ).
- There is no `CF_OPERATION_ACK_DATA_FLAG_CANCELED`; a hydration is failed by completing `TRANSFER_DATA`/`ACK_DATA` with a failing `CompletionStatus` in `CF_OPERATION_PARAMETERS`.
- `CfRegisterSyncRoot` needs `WRITE_DATA` or `WRITE_DAC` on the folder (implicit under the user profile); sync roots may not overlap or nest. Source: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfregistersyncroot
- `CfUnregisterSyncRoot` permanently deletes unhydrated placeholders and not-fully-populated directories. Unpackaged apps must call `StorageProviderSyncRootManager.Unregister` themselves at uninstall. Source: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfunregistersyncroot , https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.storageprovidersyncrootmanager.unregister?view=winrt-26100
- Known open issues (all community-reported on Microsoft Q&A, answered by external vendor moderators, no platform-engineering response): `NOTIFY_DEHYDRATE` does not fire for Explorer "Free up space" (watch `FILE_ATTRIBUTE_UNPINNED`, call `CfDehydratePlaceholder`, whose reference page is archived/404); `CfUpdatePlaceholder` heap corruption 0xC0000374 on 24H2/25H2 since Nov 2024 (repro: https://github.com/iterate-ch/CloudFiles-HeapCorrupt ); `CreateFileMapping` forces full hydration under PROGRESSIVE; Explorer holds handles on ancestor folders. Sources: https://learn.microsoft.com/en-gb/answers/questions/1694157/when-will-cf-callback-type-notify-dehydrate-be-inv , https://learn.microsoft.com/en-us/answers/questions/2120335/heap-corruption-after-using-cfupdateplaceholder , https://learn.microsoft.com/en-us/answers/questions/2103011/(bug-report)-cfapi-createfilemapping-causes-full-p
- Sync root should be in the Windows Search crawl scope for correct Status column / property values (CloudMirror code comment, not a documented requirement; verify on the target build). Source: https://learn.microsoft.com/en-us/answers/questions/96456/why-sync-root-folder-should-be-indexed-(cfapi)
- Package identity is not needed for registration, nav-pane node, Status column or built-in verbs (community-verified Nov 2026, https://github.com/B3nedikt/Tenshu/pull/6 ); it is needed for Windows 11 modern context-menu entries and manifest-declared extensions. Insider build 26220.7271 moved the hydration verbs into a per-provider flyout, so keep a tray-menu fallback. Source: https://blogs.windows.com/windows-insider/2025/11/21/announcing-windows-11-insider-preview-build-26100-7271-dev-beta-channels/

### Microsoft Graph (consumer OneDrive)

- `/me/drive/root/delta` is the only complete enumeration; track by id; use the last occurrence of duplicates; consumer deletes omit `ctag` and `size`; 410 Gone with `resyncChangesApplyDifferences` / `resyncChangesUploadDifferences` forces a full re-walk; `$select`/`$top` are baked into tokens. Source: https://learn.microsoft.com/en-us/graph/api/driveitem-delta?view=graph-rest-1.0
- Throttling: 429/503 with `Retry-After` seconds only (SharePoint does not return RateLimit headers); global Graph limit 130,000 req/10 s per app; per-user 3,000 req/5 min, 50 GB ingress/h, 100 GB egress/h are documented for business tenants only, consumer limits unpublished and may be lower. Safety delta poll no more than daily. Sources: https://learn.microsoft.com/en-us/graph/throttling-limits , https://learn.microsoft.com/en-us/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online , https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/scan-guidance
- Download: `@microsoft.graph.downloadUrl` is pre-authenticated, ~1 h lifetime ("might expire within minutes"), honours `Range` (206) but may answer 200 with the full body. Source: https://learn.microsoft.com/en-us/graph/api/driveitem-get-content?view=graph-rest-1.0
- Upload: simple PUT up to 250 MB, default `conflictBehavior=replace`; `createUploadSession` default `conflictBehavior=fail`; fragments multiples of 320 KiB, < 60 MiB, 5-10 MiB recommended, sequential, no `Authorization` header on fragment PUTs, 404 = session gone. Always set `@microsoft.graph.conflictBehavior` explicitly. Sources: https://learn.microsoft.com/en-us/graph/api/driveitem-put-content?view=graph-rest-1.0 , https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0 , https://learn.microsoft.com/en-us/graph/api/resources/driveitem?view=graph-rest-1.0
- Hashes: `quickXorHash` is the only hash guaranteed on personal and business; `sha256Hash` unsupported. Source: https://learn.microsoft.com/en-us/graph/api/resources/hashes?view=graph-rest-1.0
- `permanentDelete` is supported on personal (`POST /drives/{drive-id}/items/{item-id}/permanentDelete`, `Files.ReadWrite`); NoDrive sends remote deletes to the recycle bin by default. Source: https://learn.microsoft.com/en-us/graph/api/driveitem-permanentdelete?view=graph-rest-1.0
- `sharedWithMe` and `recent` are officially deprecated and stop returning data after November 2026; do not build on them.
- MC1492958: tempauth pre-authenticated URLs and 302 redirects retire 2027-04-01 for commercial clouds; consumer scope unstated. Keep the downloader behind an interface. Source (secondary): https://pupuweb.com/mc1492958-microsoft-graph-new-tenant-controls-for-retirement-of-non-standard-file-api-tokens/
- Push without a public endpoint: `GET /me/drive/root/subscriptions/socketIo` (WebSocket transport only, delegated, personal supported); v1 uses adaptive delta polling instead. Source: https://learn.microsoft.com/en-us/graph/api/subscriptions-socketio?view=graph-rest-1.0

### Process and memory budget (acceptance criteria, from the plan)

- Callback-to-return latency p99 < 5 ms, max < 50 ms; no I/O inside a cfapi callback.
- Idle Private Bytes under 30 MB with a 500k-item index; after a 10 GB hydration burst commit returns to within 10 MB of idle inside 60 s; flat peak during initial 500k-item delta ingestion; `HeapHardLimit` fails loudly.
- Published AOT-vs-JIT numbers (~23 MB vs ~40 MB working set on Windows x64 ASP.NET, .NET 8) are third-party single samples; no published idle number for an AOT tray/cfapi provider exists, so measure in the spike. Source: https://www.thinktecture.com/en/?p=20991 (secondary)

## 8. Diagnostics

- `Microsoft-Windows-CloudFiles/Operational` Event Viewer channel: the only built-in cfapi diagnostics; enable it during development. Community-reported (Q&A FAQ): https://learn.microsoft.com/en-us/answers/questions/2288103/cloud-file-api-faq
- Native AOT process: EventPipe via `DOTNET_EnableEventPipe` environment variables, or Windows Performance Recorder; `dotnet-counters`/`dotnet-trace` attach only to the JIT variant. PerfView for heap and GC analysis of either variant via ETW. Source: https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/
- Crash dumps: keep `DebuggerSupport=true` and ship the `.pdb`; `StackTraceLineNumberSupport` (.NET 11, PublishAot only) embeds file/line info if needed. Source: https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trimming-options
- In-process: callback-latency histogram, queue depth, `GC.GetGCMemoryInfo().TotalCommittedBytes`, thread and handle counts, logged by the supervisor thread (see systemPatterns.md).
- Fault-injecting fake Graph server (429 with long `Retry-After`, timeouts, expired `downloadUrl`, network loss) from P0 onward.

## 9. Reference material and licences

Permissive, code may be ported with attribution (NoDrive is MIT, ADR-0006):

| Project | Licence | What it is useful for | Source |
|---|---|---|---|
| rclone `backend/onedrive` (Go, v1.76.0, 2026-11-06) | MIT | Graph protocol reference: delta, QuickXorHash, 320 KiB-aligned upload sessions, restricted characters, refresh-token behaviour | https://github.com/rclone/rclone , https://rclone.org/onedrive/ |
| Microsoft CloudMirror sample (C++/WinRT) | MIT | Architectural blueprint: registrar, placeholders, chunked hydration with progress, shell services; "not production code"; no upload/remote-change propagation | https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/CloudMirror |
| styletronix/cfapiSync (C#) | MIT | SyncProvider/IServerProvider split, pin-state handling; very early alpha, last push 2025-08-18 | https://github.com/styletronix/cfapiSync |
| HRAshton/WinCfApiWrapper (C#, NuGet 1.2.2, 2026-11-12) | 0BSD | Registration/unregistration and hydration plumbing; read-only roots only | https://github.com/HRAshton/WinCfApiWrapper |
| MirrorPulse/CfSharp (C#, .NET 10) | Apache-2.0 | Sync-root/placeholder/hydration API shapes and an SQLite state store; previews only, 4 stars | https://github.com/MirrorPulse/CfSharp |
| Vanara.PInvoke.CldApi 5.0.8 (2026-10-24) and `UnitTests/PInvoke/CldApi/CloudSyncProvider.cs` | MIT | Full CldApi signatures and a minimal 13-callback skeleton; classic marshalling, not AOT-first; prototyping only | https://www.nuget.org/packages/Vanara.PInvoke.CldApi , https://github.com/dahall/Vanara/blob/master/UnitTests/PInvoke/CldApi/CloudSyncProvider.cs |
| Microsoft.Windows.CsWin32 | MIT | Binding generator (dependency) | https://github.com/Microsoft/CsWin32 |

Copyleft, read-only for protocol and behaviour knowledge, never copy code:

| Project | Licence | Why read it | Source |
|---|---|---|---|
| Nextcloud desktop `src/libsync/vfs/cfapi/` (C++) | GPL-2.0-or-later | Most battle-tested cfapi provider: `cfapiwrapper.cpp`, `hydrationjob.cpp`, registry layout, callback flags; also the 2026 Explorer-freeze regression (issue #10150) as a cautionary tale | https://github.com/nextcloud/desktop/tree/master/src/libsync/vfs/cfapi |
| ownCloud client `src/plugins/vfs/win/` (C++) | GPL-2.0-or-later (relicensed 2025) | `vfs_win.cpp`, `callbacks.cpp`, `hydrationdevice.cpp` | https://github.com/owncloud/client/tree/master/src/plugins/vfs/win |
| abraunegg/onedrive (D, v2.5.12, 2026-10-24) | GPL-3.0 | Delta-driven engine with SQLite baseline keyed by (driveId, id); known-issues list | https://github.com/abraunegg/onedrive/blob/master/docs/client-architecture.md |
| jstaf/onedriver (Go FUSE, v0.15.0) | GPL-3.0 | On-demand download filesystem design | https://github.com/jstaf/onedriver |
| PrimalZed/CloudSync (+ secdev02 fork, C#) | GPL-3.0 | C# port of CloudMirror on Vanara; large uploads "tend to break" | https://github.com/PrimalZed/CloudSync |

Not applicable: WinFsp (GPLv3 + FLOSS exception, kernel driver, commercial licence 2,400-6,000 USD) and Dokany (LGPL-3.0 driver, dokan-dotnet MIT) are mount alternatives rejected in ADR-0001. KoenZomers/OneDriveAPI is EPL-1.0 (weak copyleft) and MSAL/Graph based; SimpleList has no licence file and cannot be used.

## 10. Toolchain questions to settle in the P0 spike

1. Does the CsWinRT projection for `net11.0-windows10.0.26100.0` publish under Native AOT? Fallback: `CfRegisterSyncRoot` + registry keys, or a separate non-AOT registration helper exe.
2. Do `[UnmanagedCallersOnly]` cfapi callbacks generated by CsWin32 work end to end under AOT? No published sample exists.
3. Does Microsoft.Data.Sqlite.Core + `SQLitePCLRaw.provider.winsqlite3` publish warning-free with `PublishAot` (explicit `SQLitePCL.raw.SetProvider`)? Fallback: raw `LibraryImport` of `winsqlite3.dll`.
4. Is the `CfUpdatePlaceholder` heap corruption present on the dev build? If yes, restrict to `CfSetInSyncState`/`CfSetPinState`.
5. Does a fresh personal-account app registration hit `403 serviceReadOnly`?
6. Which delta page size does consumer OneDrive honour via `$top`, and what is the real first-sync time for ~200k items?
7. Idle Private Bytes of hello-world, tray-only and tray+HttpClient AOT variants, versus OneDrive.exe and `rclone mount` on the same PC.
