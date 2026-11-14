# Progress

**P0 COMPLETE 2026-11-14: verdict GO, see `p0-results.md`. P1 milestone 1 (read-only mount of the real OneDrive) delivered the same day: see activeContext.**

**Status (2026-11-13):** planning complete, memory bank created and reviewed, repo scaffolded and building (0 warnings) with 2/2 smoke tests green on macOS; P0 spike not started; no engine code runs yet; nothing committed to git.

## What works

- **CI run #5 (2026-11-13) AOT-published both spikes with ZERO IL2xxx/IL3xxx warnings**: `cfapi-minimal.exe` 6.06 MB (WinRT `StorageProviderSyncRootManager` projection + CsWin32 function-pointer callbacks + HttpClient/JSON), `aot-baseline.exe` 4.41 MB, `NoDrive.Engine.exe` stub 0.78 MB. The P0 kill criterion "AOT callbacks or WinRT projection unworkable on net11.0" is retired at compile time; runtime behaviour still to be proven on the PC. Caveat: run #5 binaries used default GC settings (runtimeconfig.template.json is ignored by AOT); use run #6 or later.
- **CI on windows-latest is green (run #2, 2026-11-13, 2m20s):** restore, build, tests, and a **Native AOT publish of `NoDrive.Engine` for win-x64 on .NET 11 succeeded** (artifact `engine-win-x64`, 1.87 MB zipped). The engine is a stub, so this proves the toolchain (CsWin32 build-task bindings + ILC on net11.0-windows10.0.26100.0), not the runtime behaviour. The workflow now also AOT-publishes `aot-baseline` and `cfapi-minimal` and uploads them as `spikes-win-x64`, so the WinRT-projection-under-AOT question is answered by CI and the binaries can be downloaded straight to the test PC.

- `spikes/graph-smoke` (JIT console, any OS): PKCE sign-in via browser + loopback, refresh-token grant, `/me/drive`, delta pages with `$top`, item GET with downloadUrl, two Range reads (206). P0 step 1 passed on 2026-11-13.

- `dotnet build NoDrive.slnx` compiles `NoDrive.Core`, `NoDrive.Graph`, `NoDrive.Engine` (Windows TFM, restores on macOS via EnableWindowsTargeting) and `NoDrive.Core.Tests` with 0 warnings under warnings-as-errors.
- `dotnet test NoDrive.slnx` runs 2/2 scaffold tests (ItemState byte values, 320 KiB upload alignment) on Microsoft.Testing.Platform.
- `.github/workflows/windows.yml` defines build, test and win-x64 Native AOT publish on windows-latest (not yet executed; repo has no remote).
- No engine behaviour exists: `NoDrive.Engine.Program.Main` returns 0. CsWin32 generates the Cf*/tray bindings listed in `NativeMethods.txt` but nothing calls them yet.
- Research findings with sources are recorded in `memory_bank/research/`; decisions in `memory_bank/decisions/`.

## What is left

Checkboxes mirror `projectPlan.md`. Tick here when the plan row is updated.

### P0: Spike (2-3 weeks, Windows 11 x64 PC)
- [x] 1. Entra tenant + app registration (client ID 6711e7c6-ec09-42df-a5bb-bfb449119fdc, settings verified by the user 2026-11-13); PKCE sign-in with the real account and refresh-token grant both work (`spikes/graph-smoke`, run on macOS); `/me/drive/root/delta` succeeds, no 403 serviceReadOnly; `$top=1000` honoured approximately (1019-1201 items/page); downloadUrl present on plain item GET (not with `$select`); Range requests return 206 at arbitrary offsets. Details in `research/graph-onedrive.md` ("Measured" section). Full-drive enumeration: 136,411 items (17,286 folders, 119,125 files) in 129 pages / 6.5 min, no throttling, 88 MiB JSON
- [x] 2. **DONE for the decision (2026-11-14): AOT tray idle 4.3 MB private / 14.5 MB WS (20 min); AOT HttpClient 1 GB burst peak 9.0 MB, 6.4 MB after the release GC; AOT cfapi provider idle 12.7-14.6 MB and +2.6 MB during a 1.68 GB pin hydration, +2.5 MB after 60 s; OneDrive.exe idle 209 MB private / 142 MB WS. Hello and JIT-variant rows and the rclone reference remain optional.** Earlier note: Tray variant measured 2026-11-14 on the user's PC: 4.3 MB private / 14.5 MB WS idle for 20 min, 3-5 threads** (target < 30 MB). Still to run: hello and JIT variants, the 1 GB burst (`--item ... --limit-mb 1024`), OneDrive.exe reference row, `measure.ps1` CSVs. Native AOT memory baseline on .NET 11: hello-world, tray-only, tray + 1 GB download; AOT vs JIT; OneDrive.exe and rclone mount reference rows; measurement table filled
- [x] 3. **Native AOT build verified on the PC too (2026-11-14): hydrate-on-read 1.6 s, rename/delete acks 11/9 ms, pin hydrates, unpin of a renamed (not-in-sync) file is refused by Windows with 0x80070179 (the safety rule enforced by the platform), unpin of an in-sync file dehydrates.** Pin/unpin implemented in the spike (`PinWatcher`): hydrate on pin 2.4 s, dehydrate on unpin 17 ms. Also: the spike keeps an existing registration and writes Flags=1058 only on fresh registration when elevated (ADR-0009 design).** **PASSED 2026-11-14 (unpackaged cfapi provider, user-verified in Explorer): native Status column, standard cloud icons and 'Always keep / Free up space' verbs once `Flags=1058` is written after `Register` (ADR-0009); remaining nuance: nav-pane node not explicitly confirmed, AOT runtime not yet run on the PC (CI compiles it).** SSH-driven tests 2026-11-14 (JIT build, same PC): hydration works end to end (14 KB to 1.7 GB, data intact, memory flat at 16-21 MB), rename/delete acks 8 ms, provider-down open fails in 17 ms (0x80070194), `--cleanup` 581 ms and deletes the 164 unhydrated placeholders as documented. FINDING: Windows requests the WHOLE file at open (`required=[0+size]`) even with PROGRESSIVE, so the opener blocks for the full download; investigate (see research/cfapi.md).** Still open: Explorer visual checks (nav pane, Status column, verbs), Explorer double-click/media-player behaviour. **First run 2026-11-14 on Win11 26H2 (26300.9974): `StorageProviderSyncRootManager.Register` worked unpackaged under Native AOT, `CfConnectSyncRoot` ok, 200 placeholders created, idle 10.6 MB private.** Not yet exercised: Explorer nav-pane/Status check, hydration (no FETCH_DATA seen because no file was opened), rename/delete acks, provider-kill test, `--cleanup`. Unpackaged sync root under PublishAot: Register, placeholders from a delta page, PROGRESSIVE FETCH_DATA via downloadUrl Range; nav pane, Status column, verbs and progress confirmed on the target build; zero IL2xxx/IL3xxx from our code
- [x] 4. **DONE 2026-11-14: heap corruption NOT reproduced on Win11 26H2 build 26300.9974 (14,000 CfUpdatePlaceholder calls, 3 variants incl. 4 threads, heap validate OK, no crash events); keep `cfapi-minimal --heap-repro` as a per-build regression check; use CfUpdatePlaceholder sparingly.** CfUpdatePlaceholder heap-corruption repro on the dev build; decision recorded
- [x] 5. **PASSED 2026-11-14: `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.provider.winsqlite3` + source-generated STJ publish Native AOT with zero IL2xxx/IL3xxx warnings (5.30 MB exe, no native SQLite shipped, uses System32 winsqlite3.dll 3.53.4); 500,000 rows inserted and queried on the user's PC (spikes/scale-500k).** 500k-row insert/query
- [~] 6. **USER-OBSERVED PROBLEM: on-demand population of a 31,030-child folder freezes the Explorer window 7-10 s (busy cursor, no hang state). Not tunable (batch/parallel matrix: 4.8-5.3 s provider regardless). FIX PROVEN: background prefetch (`Prefetcher`): 6.4 s off the user path, foreground listing unaffected, then cold open 1.1 s with 0 callbacks; awaiting the user's Explorer confirmation. Plan item E3c.** cfapi half measured by SSH 2026-11-14: FETCH_PLACEHOLDERS answered from the 500k index with population FULL; 31,030-child folder populated in 6.5 s (32 batches), root in 39 ms, callbacks 0.1-5 ms, +2.5 MB provider memory. Pending: the user's visual Explorer check of responsiveness while opening that folder cold.** Index half PASSED 2026-11-14 (Native AOT, user's PC): 500,000 items ingested in 60.7 s locally (network-bound against real Graph), peak private 33.0 MB, 10.3 MB after release, DB 154 MB (323 B/row), 30,030-child folder listed from the index in 153 ms (first 512 rows 3.1 ms), 100k lookups 16 us each, incremental 5,000 changes 0.87 s, interrupt+resume exact. Memory independent of item count (50k vs 500k peaks within 2 MB). STILL OPEN: the cfapi half (FETCH_PLACEHOLDERS answered from this index under real Explorer, PARTIAL population).** Fake Graph server 500k-item scale test: first-sync time, index size, peak/idle memory, 30k-child FETCH_PLACEHOLDERS latency
- [x] 7. **DONE 2026-11-14 (`spikes/remote/responsiveness.ps1`, fake server with fault injection): throttling error 0.18 s, stall error 15.2 s, reset recovered, slow-but-alive OK, kill -> error 0.2 s and clean restart, 4 concurrent hydrations OK (peak 18.3 MB); Explorer-style probes median < 5 ms, max 25 ms in every scenario.** Responsiveness harness: callback-latency histogram, pending-request age tracker, fault-injecting fake server (429/timeout/expired URL/network loss/200-instead-of-206)
- [x] Kill/go decision written: **GO** (`memory_bank/p0-results.md`); open questions answered there

### P1: v1 (2-3 months)
- [ ] E1 Registration and placeholders (PARTIAL population, PROGRESSIVE hydration, crawl scope, overlap check, never unregister on exit)
- [ ] E1b OneDrive.exe guard, former-folder detection, adoption of existing files (ADR-0008; confirmed in v1 scope)
- [ ] E2 Lazy enumeration from the index only, batched and paginated; unsupported items excluded and listed
- [ ] E3 Progressive hydration: bounded workers, Range with 200 fallback, pooled chunked TRANSFER_DATA, progress every 15 s, watchdog failure path, RECOVER and cancel handling
- [ ] E4 Delta ingestion and index: streamed `$select` delta, resumable, deletes-first, duplicates, 410 resync, adaptive polling, global Retry-After gate, User-Agent, minimal CfUpdatePlaceholder with oplocks
- [x] E5 upload of new files and folders (verified on PC)
- [x] E6 delete/rename propagation (needs PC verification)
- [ ] E7 Tray and lifecycle: tray menu with pause and free-up-space/keep-offline fallbacks, HKCU Run, single instance, restart-on-failure, graceful disconnect, refuse to run beside OneDrive.exe
- [ ] E8 Recovery and uninstall: journal replay, USN cursor reconcile, RECOVER re-drive, hydrate-or-warn before Unregister
- [ ] E9 Memory hygiene: GC knobs, HeapHardLimit, Aggressive collect after bursts, short-lived pooled connections, no idle timers
- [ ] E10 Responsiveness harness in CI; supervisor thread; all cross-cutting acceptance tests green; 24 h soak passed
- [ ] Manual Explorer checklist passed (Notepad, Photos, Office save, rename storm, Free up space, AV scan, kill mid-hydration, reboot)
- [ ] v1 tagged

### P2
- [ ] Folder backup (Known Folder Move) support for Desktop/Documents/Pictures via IKnownFolderManager::Redirect, pinned by default, reversible; shell verification matrix passed
- [ ] In-place editing with If-Match, 412 conflict copies, echo suppression, Office save-storm debounce, USN replay
- [ ] Pin/unpin handling and self-dehydration on FILE_ATTRIBUTE_UNPINNED
- [ ] Thumbnail COM local server backed by Graph /thumbnails
- [ ] StatusUI flyout (feature-detected)
- [ ] socket.io wake-up channel
- [ ] Settings window as a separate on-demand AOT process
- [ ] Metered-network, power events, bandwidth cap
- [ ] Transfer layer ready for tempauth URL retirement (2027-04-01, consumer scope unclear)

### P3 (optional)
- [ ] Signed sparse package for Windows 11 modern context-menu entries
- [ ] File Explorer Home integration when stable
- [ ] macOS (File Provider) / Linux exploration

## Known issues and risks

Nothing is implemented, so there are no product bugs yet. The items below are platform and toolchain hazards the design already accounts for; see `projectPlan.md` section 7 for mitigations.

### Platform bugs and pitfalls (cfapi / Explorer)
- `CfUpdatePlaceholder` heap corruption on Windows 11 24H2/25H2, open since Nov 2024, community-reported (https://learn.microsoft.com/en-us/answers/questions/2120335/heap-corruption-after-using-cfupdateplaceholder); repro at https://github.com/iterate-ch/CloudFiles-HeapCorrupt. Verify in P0 step 4.
- `NOTIFY_DEHYDRATE` / `_COMPLETION` do not fire for Explorer "Free up space"; watch `FILE_ATTRIBUTE_UNPINNED` and dehydrate ourselves. Community-reported (https://learn.microsoft.com/en-gb/answers/questions/1694157/when-will-cf-callback-type-notify-dehydrate-be-inv). `CfDehydratePlaceholder` is documented only on an archived page (current reference URL returns 404) but is still exported and in Win32Metadata.
- Memory-mapped reads (`CreateFileMapping`) and antivirus scans force full hydration regardless of PROGRESSIVE policy. Community-reported (https://learn.microsoft.com/en-us/answers/questions/2103011/(bug-report)-cfapi-createfilemapping-causes-full-p).
- Fixed, non-configurable 60 s callback deadline; `CfReportProviderProgress` resets it (https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfreportproviderprogress). Missing it stalls Explorer.
- `CfUnregisterSyncRoot` permanently deletes unhydrated placeholders (https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfunregistersyncroot). Never on normal exit; uninstall must hydrate or warn.
- Provider not running means every online-only file fails with 0x8007016A (community-reported: https://github.com/B3nedikt/Tenshu/pull/6); a crashed provider leaves its registration behind. The "Explorer relaunches on every click" warning applies to the packaged CloudMirror sample (its registration references a package that prompts on activation); for an unpackaged exe the effect is 0x8007016A everywhere until the engine restarts. Needs autostart and restart-on-failure.
- Hydration verbs moved into a per-provider flyout in Insider build 26220.7271 (https://blogs.windows.com/windows-insider/2025/11/21/announcing-windows-11-insider-preview-build-26100-7271-dev-beta-channels/); KB5127936 lets users hide context-menu entries (community-reported, third-party press: https://www.bleepingcomputer.com/news/microsoft/windows-11-kb5127936-update-released-with-41-changes-and-fixes/). Tray fallbacks required.
- Sync root must not be a drive root, NTFS only, must not overlap another root (https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine). Indexing requirement for correct Status column is community-reported from a CloudMirror code comment (https://learn.microsoft.com/en-us/answers/questions/96456/why-sync-root-folder-should-be-indexed-(cfapi)).
- `CVE-2025-50170` changed cldflt.sys placeholder-creation behaviour in Aug 2025 (https://starlabs.sg/advisories/25/25-50170/): minimum supported build is 24H2 with Aug 2025 CU.
- Double delete callbacks (Recycle Bin, replace-on-save), new local files not auto-converted to placeholders, overwriting a placeholder stripping the reparse point. Community-reported FAQ (https://learn.microsoft.com/en-us/answers/questions/2288103/cloud-file-api-faq). Note the FAQ's `CF_OPERATION_ACK_DATA_FLAG_CANCELED` does not exist; fail a hydration via a failing `CompletionStatus`.
- All "Microsoft confirmed" statements from Microsoft Q&A are from vendor moderators (Microsoft External Staff); GitHub Windows-classic-samples issue #245 has zero Microsoft replies.

### Toolchain uncertainties (.NET 11 / Native AOT)
- No end-to-end sample of cfapi function-pointer callbacks under Native AOT; CsWin32 0.3.371 AOT recipe is documented (`CsWin32RunAsBuildTask`, `DisableRuntimeMarshalling`, `allowMarshaling=false`: https://microsoft.github.io/CsWin32/docs/getting-started.html) but unproven for CldApi here.
- CsWinRT 2.3.1 documents .NET 9/10 only; .NET 11 TFM projection unverified (https://github.com/microsoft/CsWinRT/releases). Windows SDK TFMs with revision `1` pull the CsWinRT 3.0 preview (NETSDK1229).
- Runtime Async is on inside the BCL regardless of `UseRuntimeAsync` (https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime); stability on an STS release is a v1 consideration.
- Microsoft.Data.Sqlite AOT status not officially stated (https://github.com/dotnet/efcore/issues/29725); verify in P0 step 5.
- `IsAotCompatible` metadata only exists for .NET 10+ libraries, so `VerifyReferenceAotCompatibility` will flag older packages regardless of real behaviour (https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/). Inventory, not gate.
- `DebuggerSupport=false` also strips symbols; keep it true for symbolicated crash dumps (https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trimming-options).
- No published idle-memory number for a Windows AOT tray/cfapi process, OneDrive.exe or rclone mount; all must be measured locally.
- MSAL WAM broker (`Microsoft.Identity.Client.Broker` + NativeInterop, netstandard2.0) is not AOT-analysed and msalruntime.dll cannot be bundled single-file (community-reported, https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/issues/6232). Not used in v1.
- Microsoft.Extensions.Hosting AOT status unverified; not used.
- Native AOT cannot cross-compile from macOS (https://learn.microsoft.com/dotnet/core/deploying/native-aot/cross-compile); all publishes on the Windows PC or a windows-latest runner.

### Graph / consumer account
- Newly consented personal-account apps reported to get 403 `serviceReadOnly` / 503 provisioning, no Microsoft reply (community-reported, https://learn.microsoft.com/en-us/answers/questions/5983388/personal-onedrive-newly-consented-apps-get-403-acc). P0 step 1.
- No published throttling numbers for consumer OneDrive; SharePoint page covers licensed tenants and says limits may be lower for unpaid usage (https://learn.microsoft.com/en-us/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online). Retry-After mandatory, RateLimit headers optional.
- tempauth pre-authenticated URL retirement 2027-04-01 for commercial clouds; consumer scope unclear (third-party summary of MC1492958: https://pupuweb.com/mc1492958-microsoft-graph-new-tenant-controls-for-retirement-of-non-standard-file-api-tokens/).
- `sharedWithMe` and `recent` officially deprecated; stop returning data after Nov 2026 (Graph v1.0 reference pages carry the banner).
- `downloadUrl` expires in about an hour; Range may be ignored with a 200 full body (https://learn.microsoft.com/en-us/graph/api/driveitem-get-content?view=graph-rest-1.0).
- socket.io channel reliability for personal accounts unverified (the Q&A evidence returns 410 Gone).

## Decision evolution

| Date | Decision | Reason | ADR |
|---|---|---|---|
| 2026-11-13 | cfapi provider instead of a custom Explorer-like client | Only route to placeholders, Status icons, nav-pane node and transparent access from any app; headless engine keeps idle memory minimal | `decisions/` ADR-0001 |
| 2026-11-13 | .NET 11 + Native AOT first; fallback .NET 10 LTS AOT, then trimmed JIT | Smallest resident footprint; .NET 11 GA 2026-11-10 with the same support end as .NET 10 (Nov 2028); spike gates the risk | ADR-0002 |
| 2026-11-13 | Hand-rolled auth-code + PKCE + system browser + loopback + DPAPI | Zero dependencies, provably AOT-clean; WAM broker not AOT-analysed and not single-file | ADR-0003 |
| 2026-11-13 | No Microsoft.Graph SDK; HttpClient + STJ source generators | SDK is 52 MB, IsTrimmable but not IsAotCompatible; only about 10 endpoints needed | ADR-0004 |
| 2026-11-13 | Index in SQLite via system winsqlite3.dll | Zero native payload; disk-backed tree so memory is independent of item count; AOT to verify in P0 | ADR-0005 |
| 2026-11-13 | MIT licence | Keeps all permissive sources usable; GPL projects become protocol references only | ADR-0006 |
| 2026-11-13 | PopulationPolicy PARTIAL with lazy FETCH_PLACEHOLDERS; design for 500k items | User's drive had 400k+ items; pre-creating every placeholder is minutes of I/O and a huge index; folder opens must stay fast | ADR-0007 |
| 2026-11-13 | v1 scope: browse, download, upload new files; editing in P2 | Editing roughly doubles engine complexity (conflicts, eTag races, Office storms, USN replay); ship a reliable read-mostly client first | plan |
| 2026-11-13 | NoDrive replaces OneDrive.exe; refuse to start while linked | Two engines on one account duplicate storage and fight over changes | plan |
| 2026-11-13 | Unpackaged in v1, registry-based shell integration | Package identity only needed for Windows 11 modern context-menu entries and manifest extensions | plan |
| 2026-11-13 | HydrationPolicy PROGRESSIVE (Range on downloadUrl), AUTO_DEHYDRATION_ALLOWED | FULL forces whole-file download on first byte access, conflicting with the low-footprint goal for large media; Range is documented | plan |
| 2026-11-13 | Responsiveness and correctness are hard requirements above features; 9 design rules, 6 acceptance tests | User's OneDrive.exe experience: 4 GB RAM, Explorer hangs, dialogs stuck at "0 files remaining" | plan section "Non-negotiable" |
| 2026-11-13 | Windows 11 only, 24H2 + Aug 2025 CU minimum | cldflt.sys changed in the Aug 2025 patch; Windows 10 22H2 under ESU shares the API baseline but is untested (effort, not support, is the reason) | plan |
| 2026-11-13 | Remote deletes to recycle bin only; mass-delete threshold pauses sync | Data-loss guard; permanentDelete exists on personal but is never the default | plan |

## Known issue: large-video playback in Photos (deferred 2026-11-14)
Undownloaded ~3.5 GB mp4 in Photos plays choppy; seeking (e.g. minute 5) fails with "can't play the file"; pause once hung the player.
Tried: read-ahead (1 to 32 MiB), RequestKey-keyed cancellation, two request lanes (explorer/search/AV in background lane, no read-ahead). Not fixed.
Evidence: single connection about 14 MB/s; explorer thumbnails fetched 12x requested bytes; no Photos.exe entries in caller stats.
Next ideas when revisited: per-request timing log for Photos, parallel segment download (`--bench`), verify every request completes on cancel/ReadAheadStopped paths. Workaround: "Always keep on this device" for videos.
