# NoDrive project plan (living document)

Phased plan derived from the approved plan of 2026-11-13. Scope, non-goals and confirmed decisions live in `projectbrief.md`; architecture in `systemPatterns.md`; stack and versions in `techContext.md`. This file holds the *sequence*: what is done when, how each step is judged, and what kills or unblocks the next phase. Checkbox status mirrors `progress.md`.

Source conventions: every API/limit/version claim carries a URL from the 2026-11-13 research pass, or is marked **community-reported** (Microsoft Q&A answers by "Microsoft External Staff" vendor moderators, GitHub issues, forum threads) or **to verify in spike**.

## 0. Ground rules that outrank every phase

1. **Responsiveness and correctness outrank features.** NoDrive must never hang or block File Explorer, never leave a dialog stuck, never balloon memory. These are measured acceptance criteria (section 6), not aspirations.
2. **Design for 500k items** (the user's drive had 400k+, has about 200k today): lazy population, disk-backed index, streaming everywhere, idle memory independent of item count.
3. **Spike before committing.** P0 exists to retire the two platform/toolchain risks (cfapi on 24H2/25H2, Native AOT on .NET 11) before any engine code depends on them.
4. **Permissive sources only** (MIT licence): rclone, CloudMirror, cfapiSync, WinCfApiWrapper, CfSharp, Vanara, CsWin32 may be read and ported; Nextcloud, ownCloud, abraunegg/onedrive, onedriver, PrimalZed/CloudSync (GPL-3.0, https://github.com/PrimalZed/CloudSync) are protocol references only.

## 1. Phase overview

| Phase | Goal | Duration | Gate |
|---|---|---|---|
| P0 Spike | Prove cfapi + Native AOT + .NET 11 + unpackaged registration + 500k-scale index on the user's Windows 11 x64 PC | 2-3 weeks | Kill/go criteria (section 2.4) |
| P1 v1 | Browse (lazy placeholders), download (progressive hydration), upload new files, rename/delete propagation with guards, tray, recovery, uninstall, memory hygiene, responsiveness harness green | 2-3 months | All cross-cutting acceptance tests (section 6) pass on the PC |
| P2 | In-place editing with conflict copies and USN replay, pin/unpin, thumbnails, StatusUI flyout, socket.io wake-up, settings window | after v1 | per-epic |
| P3 (optional) | Signed sparse package for modern context-menu entries; macOS/Linux exploration | undecided | none |

## 2. P0: Spike (2-3 weeks, Windows 11 x64 PC)

Everything in P0 is throwaway measurement code under `spikes/`. Nothing graduates to `src/` until the gate is passed. All AOT publishes must run on Windows: Native AOT does not cross-compile from macOS (https://learn.microsoft.com/dotnet/core/deploying/native-aot/cross-compile).

### 2.1 Steps (numbered; order matters for 1-3, the rest can interleave)

Exact commands, prerequisites and manual checks for each step are in `p0-runbook.md`.

1. **Graph access with a fresh personal app.** Create a free Entra tenant (a personal MSA alone cannot create app registrations: community-reported, https://learn.microsoft.com/en-us/answers/questions/5693812/app-registration-option-disabled-for-my-account-in), register a public client with signInAudience `AzureADandPersonalMicrosoftAccount`, redirect `http://localhost`, scopes `Files.ReadWrite offline_access`. Sign in with the real personal account (freshly consenting to the new app; also try a throwaway personal account if it fails, to separate app from account effects) via hand-rolled auth-code + PKCE + loopback listener, call `GET /me/drive/root/delta` (https://learn.microsoft.com/en-us/graph/api/driveitem-delta?view=graph-rest-1.0). **Purpose:** rule out the 403 `serviceReadOnly` / 503 provisioning failure reported for newly consented personal apps (community-reported, open since 2026-08, https://learn.microsoft.com/en-us/answers/questions/5983388/personal-onedrive-newly-consented-apps-get-403-acc). Also record which `$top` the consumer delta honours.
2. **Native AOT memory baseline on .NET 11.** Three variants, each published `-r win-x64 -p:PublishAot=true` and also framework-dependent JIT for comparison: (a) hello-world WinExe, (b) message-only window + `Shell_NotifyIcon` tray via CsWin32, (c) tray + single `SocketsHttpHandler` downloading 1 GB to disk with streaming. Record Private Bytes (commit), Working Set, thread count, handle count at 2 min and 10 min idle and immediately after and 60 s after the 1 GB download, with the GC settings from `techContext.md` (workstation, Concurrent=false, ConserveMemory 5-7, `System.GC.RegionSize` 1 MB via runtimeconfig since .NET 10, https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector). Measure OneDrive.exe and `rclone mount` idle on the same PC. Measure with both the Windows thread pool (AOT default) and `UseWindowsThreadPool=false` (https://learn.microsoft.com/en-us/dotnet/core/runtime-config/threading). No published idle number exists for this kind of process; the ASP.NET figures (about 23 MB AOT vs 40 MB JIT working set) are order-of-magnitude hints only.
3. **Unpackaged sync root end to end under PublishAot.** `StorageProviderSyncRootManager.Register` from the net11.0-windows10.0.26100.0 TFM (https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.storageprovidersyncrootmanager.register?view=winrt-26100), root at `%USERPROFILE%\NoDrive` added to the Search crawl scope (community-reported requirement from the CloudMirror sample, https://learn.microsoft.com/en-us/answers/questions/96456/why-sync-root-folder-should-be-indexed-(cfapi)), `CfConnectSyncRoot` with `REQUIRE_PROCESS_INFO | BLOCK_SELF_IMPLICIT_HYDRATION` (https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfconnectsyncroot), placeholders created from one delta page, `FETCH_DATA` served with PROGRESSIVE hydration from `@microsoft.graph.downloadUrl` Range requests (206, tolerate 200 full body, https://learn.microsoft.com/en-us/graph/api/driveitem-get-content?view=graph-rest-1.0). Callbacks as `[UnmanagedCallersOnly]` function pointers generated by CsWin32 0.3.371 with `CsWin32RunAsBuildTask`, `DisableRuntimeMarshalling`, `allowMarshaling=false` (https://microsoft.github.io/CsWin32/docs/getting-started.html). **Confirm on the target build:** nav-pane node, Status column icons, built-in "Always keep on this device / Free up space" verbs (which Insider build 26220.7271 moved into a per-provider flyout: https://blogs.windows.com/windows-insider/2025/11/21/announcing-windows-11-insider-preview-build-26100-7271-dev-beta-channels/), hydration progress in Explorer, zero IL2xxx/IL3xxx warnings from our code. If the CsWinRT projection fails under AOT on .NET 11 (CsWinRT 2.3.1 documents .NET 9/10 only: https://github.com/microsoft/CsWinRT/releases), fall back to `CfRegisterSyncRoot` + `SyncRootManager` registry keys (https://learn.microsoft.com/en-us/windows/win32/shell/integrate-cloud-storage) or isolate registration in a tiny JIT helper exe.
4. **CfUpdatePlaceholder heap-corruption repro.** Run https://github.com/iterate-ch/CloudFiles-HeapCorrupt on the dev build (community-reported, open since Nov 2024, https://learn.microsoft.com/en-us/answers/questions/2120335/heap-corruption-after-using-cfupdateplaceholder). Decide: avoid `CfUpdatePlaceholder` in favour of `CfSetInSyncState` / `CfSetPinState` plus recreate-on-change, or use it with a documented guard. Record the exact OS build and cumulative update.
5. **SQLite via winsqlite3 under AOT.** `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.provider.winsqlite3` (https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions); AOT status unofficial (https://github.com/dotnet/efcore/issues/29725). Verify a warning-free publish and a 500k-row insert/query. Fallback: raw P/Invoke to `winsqlite3.dll`, then a custom binary store.
6. **Scale test against a fake Graph server.** Synthetic 500k-item delta feed (about 1,000-2,500 pages at 200/page). Measure first-sync wall time, index size on disk, peak and idle Private Bytes during ingestion, and `FETCH_PLACEHOLDERS` latency for a 30k-child folder answered from the index alone. Ingestion must stream page by page into SQLite transactions with bounded buffers and resume from a persisted nextLink.
7. **Responsiveness harness.** In-process callback-latency histogram (callback entry to return), pending-request age tracker, and the fault-injecting fake Graph server (429 with Retry-After 120 s, timeouts, expired downloadUrl, mid-stream network loss, 200-instead-of-206). This harness is reused by every later phase; it is a P0 deliverable, not a P1 nice-to-have.

### 2.2 Deliverables

- `spikes/` projects for steps 2, 3, 5, 6, 7 with a `README.md` per spike recording command lines and results.
- Measurement table (section 2.3) filled in and copied into `progress.md`.
- Screenshots: Explorer nav pane, Status column, hydration progress, context-menu or flyout verbs.
- AOT publish log with zero IL2xxx/IL3xxx warnings from NoDrive code (library warnings inventoried, not gated: `IsAotCompatible` metadata only exists for .NET 10+ libraries, https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).
- A written decision per open question in section 8.
- Fake Graph server and latency histogram checked into `tests/` (the only P0 code that survives).

### 2.3 Measurement table template

Fill one row per variant; all on the same PC, same OS build, Defender on, AC power. Private Bytes = `Process.PrivateMemorySize64` cross-checked with Task Manager "Commit size"; do not report Working Set alone.

| Variant | Build | Idle 2 min: Private Bytes | Idle 10 min: Private Bytes | Working Set (10 min) | Threads (10 min) | Handles (10 min) | After 1 GB download: peak Private Bytes | 60 s after download: Private Bytes | Notes |
|---|---|---|---|---|---|---|---|---|---|
| hello-world WinExe | AOT | | | | | | n/a | n/a | |
| hello-world WinExe | JIT fx-dep | | | | | | n/a | n/a | |
| tray only | AOT | | | | | | n/a | n/a | |
| tray only | JIT fx-dep | | | | | | n/a | n/a | |
| tray + HttpClient 1 GB | AOT | | | | | | | | GC knobs as techContext |
| tray + HttpClient 1 GB | AOT, UseWindowsThreadPool=false | | | | | | | | |
| tray + HttpClient 1 GB | JIT fx-dep | | | | | | | | |
| tray + cfapi connected, 0 items | AOT | | | | | | | | CldApi.dll owns a threadpool |
| tray + cfapi + 500k-item index idle | AOT | | | | | | | | must be within budget of previous row |
| OneDrive.exe (same account, idle) | n/a | | | | | | | | reference |
| rclone mount (same remote, idle) | n/a | | | | | | | | reference |

Targets to compare against: idle Private Bytes under 30 MB with a 500k-item index; after the burst, commit returns to within 10 MB of idle inside 60 s.

### 2.4 Kill / go criteria

Status 2026-11-13: the toolchain half is answered. CI AOT-published `cfapi-minimal` (WinRT projection + cfapi callbacks) on .NET 11 win-x64 with zero IL2xxx/IL3xxx warnings (run #5). Remaining: runtime behaviour of registration, placeholders and hydration on the user's build (step 3), and the memory numbers (step 2).


**Kill (fall back, do not proceed with the current stack):**
- cfapi callbacks as AOT function pointers, or the WinRT registration projection, cannot be made to work on net11.0 after reasonable effort. Fallback order: .NET 10 LTS AOT (same end of support, Nov 2028: https://devblogs.microsoft.com/dotnet/announcing-dotnet-11/), then framework-dependent trimmed JIT.
- Unpackaged `StorageProviderSyncRootManager.Register` does not produce the nav-pane node or Status column on the target build and the `CfRegisterSyncRoot` + registry fallback does not either.
- Step 1 returns 403 `serviceReadOnly` persistently for the fresh app and no workaround (older app, different scopes) is found: the project cannot reach the user's data.

**Go (proceed to P1):**
- Steps 1-5 pass; step 6 shows idle memory independent of item count and `FETCH_PLACEHOLDERS` for 30k children well inside the 60 s deadline (https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfreportproviderprogress); step 7 harness runs.
- A written answer exists for every section 8 question, including "unknown, mitigated by X".

## 3. P1: v1 (2-3 months)

v1 = browse, download, upload new files. No in-place editing of existing files (P2). NoDrive replaces OneDrive.exe for the account and refuses to start while OneDrive.exe is linked to the same account.

Each epic lists acceptance criteria. "Harness" = the P0 responsiveness harness; "fake server" = the fault-injecting fake Graph server.

### E1. Registration and placeholders
- Register one sync root at `%USERPROFILE%\NoDrive` (never a drive root, NTFS only: https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine), PopulationPolicy PARTIAL, HydrationPolicy PROGRESSIVE, `AUTO_DEHYDRATION_ALLOWED`, InSync on LastWriteTime. Refuse to register over an existing/overlapping root (`CfGetSyncRootInfoByPath`). Add root to the Search crawl scope.
- Re-register with update semantics on every start; never unregister on normal exit (`CfUnregisterSyncRoot` permanently deletes unhydrated placeholders: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfunregistersyncroot).
- Acceptance: fresh install shows nav-pane node, Status column, verbs; restart of the engine re-attaches without re-creating placeholders; attempt to register inside an existing OneDrive root fails with a clear message.

### E1b. OneDrive.exe guard, folder detection and adoption (ADR-0008; confirmed in v1 scope 2026-11-13)
- Start-up guard: refuse to connect while `HKCU\...\OneDrive\Accounts\Personal` exists, a `SyncRootManager\OneDrive!<SID>!Personal*` key exists, or `CfGetSyncRootInfoByPath` reports a foreign provider on the configured root; tray balloon with unlink steps, exit code 3. Business-only OneDrive allowed.
- First-run folder choice: offer `UserFolder` / `%OneDriveConsumer%` / `%USERPROFILE%\OneDrive` (if not a live sync root) and `%USERPROFILE%\NoDrive`.
- Adoption worker after the first delta pass: size+mtime match -> `CfConvertToPlaceholder` + `CfSetInSyncState` (no download), lazy quickXorHash verification, `AdoptedUnverified` never dehydrated; mismatch -> `<name> (local copy YYYY-MM-DD)` + cloud placeholder; unknown -> upload-new; known-folder redirection (detected via `SHGetKnownFolderPath` default-path comparison for Desktop/Documents/Pictures/Screenshots/Camera Roll) under the root aborts adoption with a message linking to the README migration steps.
- Acceptance: on a folder with 50k ordinary files matching the index, adoption finishes without any Graph download, Explorer stays responsive (latency histogram unchanged), memory stays within the idle budget + transfer buffers; a deliberately modified file becomes a local copy, not an overwrite; `--status` reports OneDrive presence and the `DisableFileSyncNGSC` policy value.

### E2. Lazy enumeration (FETCH_PLACEHOLDERS)
- Answer only from the SQLite index, in batches, paginated; never call Graph on the callback path. Callback body enqueues and returns.
- Exclude unsupported items (Personal Vault, OneNote package facet, Windows-invalid names, case collisions) and surface them in a conflict/unsupported list instead of creating placeholders.
- Acceptance: folder with 30k children opens with harness p99 callback latency under 5 ms; Explorer shows contents progressively; same test with the fake server offline behaves identically (index only).

### E3. Progressive hydration (FETCH_DATA / CANCEL_FETCH_DATA)
- Worker pool of 3-4 hydrations; Range requests on a per-item refreshed `downloadUrl`; 200-full-body treated as "write ahead from offset 0"; chunks streamed from HTTP into `CfExecute TRANSFER_DATA` with pooled fixed-size buffers; `CfReportProviderProgress` at least every 15 s; watchdog deadline well under 60 s that completes the request with a failing `CompletionStatus` (there is no `CF_OPERATION_ACK_DATA_FLAG_CANCELED`; only `_NONE` exists).
- Handle `CF_CALLBACK_FETCH_DATA_FLAG_RECOVER` after a provider restart. Honour cancellation via `CANCEL_FETCH_DATA`.
- Acceptance: open an online-only file in Notepad and Photos; copy a 2 GB online-only file (progress dialog shows progress); pull the network mid-copy and get a visible Explorer error within 10 s, no hang; kill the engine mid-hydration, restart, re-open the file successfully.

### E4. Delta ingestion and index
- Initial `/me/drive/root/delta` with `$select`, streamed page by page into SQLite transactions, persisted nextLink/deltaLink, resumable, low priority, bounded buffers. Track by id, deletes first, last occurrence wins for duplicates, handle 410 Gone with both resync codes (https://learn.microsoft.com/en-us/graph/api/driveitem-delta?view=graph-rest-1.0). Consumer deletes omit ctag/size.
- Index schema: graphId, parentId, name, size, eTag, cTag, quickXorHash, lastModified, NTFS FileId, USN, state, plus deltaLink and a mutation journal table.
- Adaptive polling: 1 min active, backing off to 15-60 min idle, daily safety full delta (Microsoft recommends notifications + delta with a daily check: https://learn.microsoft.com/en-us/onedrive/developer/rest-api/concepts/scan-guidance). Single global Retry-After gate for all workers; `User-Agent: ISV|<owner>|NoDrive/<version>` (https://learn.microsoft.com/en-us/sharepoint/dev/general-development/how-to-avoid-getting-throttled-or-blocked-in-sharepoint-online).
- Remote changes applied to placeholders with the minimum of `CfUpdatePlaceholder` (per P0 step 4 decision) using `CfOpenFileWithOplock` so user handles are never fought over.
- Acceptance: 500k-item fake delta ingests with flat peak memory and a resumable interruption at any page; unit tests for duplicates, deletes-first, 410 resync; real account first sync completes and the time is recorded.

### E5. New-file upload
- Local watcher (`ReadDirectoryChangesW`) detects new files; debounce 2-5 s; simple PUT below a small threshold, upload session above it with 320 KiB-multiple chunks of 5-10 MiB, each PUT under 60 MiB, no Authorization header on fragment PUTs, 404 = restart session (https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0). Explicit `@microsoft.graph.conflictBehavior=fail` on every new-file write (session default is `fail`, simple PUT default is `replace`: https://learn.microsoft.com/en-us/graph/api/resources/driveitem?view=graph-rest-1.0). Compute quickXorHash while streaming and verify (https://learn.microsoft.com/en-us/graph/api/resources/hashes?view=graph-rest-1.0).
- After upload: `CfConvertToPlaceholder` + `CfSetInSyncState` (new local files are not auto-converted: community-reported, https://learn.microsoft.com/en-us/answers/questions/2288103/cloud-file-api-faq). Sanitize restricted names before upload and surface collisions as conflicts.
- Journal every mutation in SQLite before execution; replay after crash.
- Acceptance: drop 1,000 small files and one 5 GB file into the root; all appear remotely with matching hashes; kill the engine mid-upload, restart, upload resumes from `nextExpectedRanges`; a name collision produces a visible conflict entry, not a silent rename. Unit tests cover fragment alignment (320 KiB multiples, under 60 MiB) and resume from `nextExpectedRanges`.

### E6. Rename/delete propagation with guards
- `NOTIFY_DELETE` / `NOTIFY_RENAME` acked immediately from local state; remote operation journaled and executed asynchronously. Double delete callbacks (Recycle Bin, replace-on-save) are idempotent by RequestKey/FileId.
- Guards: remote deletes go to the OneDrive recycle bin only (`permanentDelete` exists on personal, https://learn.microsoft.com/en-us/graph/api/driveitem-permanentdelete?view=graph-rest-1.0, but is never used by default); mass-delete threshold (N items or X% of the index in one batch) pauses sync and asks; move-out of the root: a hydrated item is acked and journaled as a remote `Delete`; a dehydrated placeholder is denied (`ACK_RENAME` with a failure status, the tray explains how to hydrate first) because hydrating inside the ack would block Explorer's dialog, and P2 adds a CopyHook warning (see `systemPatterns.md` 3e); never dehydrate a NotInSync file.
- Acceptance: move a folder of 10k placeholders inside the root and then out of it; the Explorer dialog completes in local-move time, remote reconciles later with zero duplicates or conflicts; delete 60% of the index in one shot and sync pauses with a prompt.

### E7. Tray and lifecycle
- Message-only window + `Shell_NotifyIcon` menu: status, pause/resume, "Free up space in folder..." and "Keep folder offline" fallbacks (calling `CfSetPinState` / `CfDehydratePlaceholder` ourselves, because the built-in verbs may have moved into a flyout and `NOTIFY_DEHYDRATE` does not fire on "Free up space": community-reported, https://learn.microsoft.com/en-gb/answers/questions/1694157/when-will-cf-callback-type-notify-dehydrate-be-inv), sign in/out, open folder, quit.
- HKCU Run autostart, single-instance mutex, Task Scheduler restart-on-failure, `CfDisconnectSyncRoot` on `WM_QUERYENDSESSION`, refuse to start while OneDrive.exe is linked to the same account.
- Acceptance: reboot and the engine is back before Explorer needs it; two launches yield one process; log off/on cleanly.

### E8. Recovery and uninstall
- Startup: re-attach registration, replay journal, reconcile USN cursor for changes made while stopped, re-drive pending hydrations via RECOVER.
- Uninstall flow: disconnect, hydrate-all prompt or explicit warning, `StorageProviderSyncRootManager.Unregister` (unpackaged apps must call it themselves), remove registry/autostart.
- Acceptance: kill -9 during hydration and during delta ingestion, restart: no stuck placeholders, no lost journal entries, no Explorer restart; uninstall leaves either a fully hydrated folder or a user-acknowledged empty one.

### E9. Memory hygiene
- GC: workstation, Concurrent=false, ConserveMemory 5-7, RegionSize 1 MB, HeapHardLimit set so a leak fails loudly. After each burst: dispose, return buffers, `GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive)` (https://learn.microsoft.com/en-us/dotnet/api/system.gc.collect?view=net-11.0), allow a second collect because .NET 10+ ages UOH regions over two gen2 GCs; no periodic collects while idle; no sub-minute timers.
- Networking: one `SocketsHttpHandler`, `PooledConnectionIdleTimeout` 20-30 s, `ResponseHeadersRead` streaming; `ShouldEvictConnection` is `[Experimental("SYSLIB5008")]` and is not used in v1 (https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.shouldevictconnection?view=net-11.0).
- Acceptance: idle Private Bytes under 30 MB with the 500k index; within 10 MB of idle 60 s after a 10 GB hydration burst; flat peak during 500k ingestion.

### E10. Responsiveness harness (continuous)
- The P0 histogram and fake server run in CI on the Windows PC (or windows-latest runner) for every release; a supervisor thread inside the engine watches callback latency, queue depth and commit and fails/restarts rather than leaving Explorer waiting.
- Acceptance: all section 6 tests green; the 24 h soak passes before tagging v1.

## 4. P2

- **Folder backup (Known Folder Move) support**, opt-in per folder (Desktop, Documents, Pictures): `IKnownFolderManager::Redirect` into `<root>\<Folder>` with `COPY_CONTENTS | DEL_SOURCE_CONTENTS | WITH_UI`, folder pinned (always keep on this device) by default so Desktop items are never online-only, refuse when `GetRedirectionCapabilities` reports a policy denial or the folder contains reparse points/open files, `SHChangeNotify` then prompt for an Explorer restart; reversible via Redirect back to the default path. Release gate: verification matrix in `research/known-folder-move.md` section 6 (Desktop view, wallpaper, Start/taskbar shortcuts, Search, Storage Sense, Office save into Documents, screenshots). The user runs folder backup today, so this is the first P2 item.

- In-place editing of existing files: If-Match eTag on every PUT, 412 -> conflict copy `<name> (conflicted copy <date>)`, echo suppression via stored cTag/eTag, Office save-storm debounce, USN journal replay for offline edits.
- Pin/unpin handling (watch `FILE_ATTRIBUTE_UNPINNED`, self-dehydrate), validation of hydrated data.
- Thumbnail COM local server backed by Graph `/thumbnails` (promote to late v1 if photos matter to the user).
- StatusUI flyout (`IStorageProviderStatusUISourceFactory`, registry-registrable for unpackaged apps; feature-detect, present from build 23504+).
- socket.io change wake-up (`/me/drive/root/subscriptions/socketIo`, WebSocket only, https://learn.microsoft.com/en-us/graph/api/subscriptions-socketio?view=graph-rest-1.0) replacing most polling.
- Settings window as a separate on-demand process (Avalonia 12 or WinUI 3, both AOT-capable) launched via .NET 11 `StartDetached` (https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries).
- Metered-network and power-event awareness; bandwidth cap.
- Prepare for tempauth URL retirement 2027-04-01 (commercial clouds; consumer scope unclear): transfer layer behind an interface.

## 5. P3 (optional)

- Signed sparse package (self-signed cert in CurrentUser\TrustedPeople) only if Windows 11 modern context-menu entries are wanted; menu DLL would be native in-proc.
- File Explorer Home integration via StorageProvider APIs once it leaves Insider instability.
- macOS (File Provider) / Linux exploration.

## 6. Cross-cutting acceptance tests ("never hang Explorer")

Run on the Windows PC before every release; automated where possible via the harness.

1. **Callback latency:** callback-entry-to-return p99 under 5 ms, max under 50 ms, over a synthetic 500k-item drive.
2. **Offline/throttled Explorer operations:** with the fake server returning 429 Retry-After 120 s, or the network pulled mid-hydration: open, copy, move, rename and delete of placeholders complete or fail with a visible error within 10 s; no stuck dialog, no Explorer hang.
3. **Large move:** move a folder of 10k placeholders inside the sync root and out of it; the dialog completes in local-move time; remote reconciliation finishes later without conflicts or duplicates.
4. **Crash mid-work:** kill the engine during hydration and during delta ingestion; restart; no stuck placeholders, no lost journal entries, Explorer recovers without restart.
5. **Memory:** idle Private Bytes within a fixed budget regardless of item count (target under 30 MB with a 500k index); after a 10 GB hydration burst, commit within 10 MB of idle inside 60 s; flat peak during 500k ingestion; HeapHardLimit fails loudly.
6. **Soak:** 24 h of random Explorer operations against the fake server with injected faults: zero pending requests older than 60 s, zero provider crashes, zero Explorer restarts.
7. **Manual checklist (P1 exit):** open online-only file in Notepad and Photos; Office save over a hydrated file (v1: the replace-on-save sequence is detected and surfaced, not synced); rename storm; "Free up space"; AV full scan over the root; provider killed mid-hydration; reboot.

## 7. Risks and mitigations

| # | Risk | Evidence | Mitigation |
|---|---|---|---|
| R1 | cfapi platform fragility, no real vendor support | CfUpdatePlaceholder heap corruption 24H2/25H2 (community-reported, https://learn.microsoft.com/en-us/answers/questions/2120335/heap-corruption-after-using-cfupdateplaceholder); NOTIFY_DEHYDRATE never fires (community-reported); CfDehydratePlaceholder documented only on an archived page; verbs moving into flyouts; all "Microsoft confirmed" statements are from vendor moderators | Minimise CfUpdatePlaceholder (P0 step 4); pin minimum build 24H2 + Aug 2025 CU (CVE-2025-50170 changed the placeholder-creation path in cldflt.sys: https://starlabs.sg/advisories/25/25-50170/); keep a repro suite; tray fallbacks for verbs; enable the Microsoft-Windows-CloudFiles/Operational log in dev |
| R2 | Native AOT on a 3-day-old .NET 11 STS for interop-heavy code | No end-to-end sample of cfapi function-pointer callbacks under AOT; CsWinRT 2.3.1 documents .NET 9/10 only; Runtime Async is baked into the BCL regardless of `UseRuntimeAsync` (https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime); Microsoft.Data.Sqlite AOT unofficial; Hosting AOT status unverified (not used) | P0 steps 2, 3, 5 first; fall back to .NET 10 LTS AOT, then trimmed JIT; isolate WinRT registration in a helper exe if needed |
| R3 | Sync correctness and data loss | Delete without guards, dehydrating dirty files, replace-on-save stripping reparse points, eTag races, CfUnregisterSyncRoot deleting unhydrated placeholders, double-syncing alongside OneDrive.exe | Per-item state machine, If-Match on every write, conflict copies, mass-delete threshold, never dehydrate NotInSync, journaled mutations, uninstall flow, refuse to run beside OneDrive.exe |
| R4 | Consumer Graph uncertainty | 403 serviceReadOnly for new apps (community-reported, no Microsoft reply); no published consumer throttling numbers (SharePoint page covers licensed tenants only); tempauth retirement 2027-04-01 consumer scope unstated; sharedWithMe/recent officially deprecated after Nov 2026 | Validate day one (P0 step 1); transfer layer behind an interface; global Retry-After gate; never build on sharedWithMe/recent |
| R5 | Explorer freezes from slow callbacks (the OneDrive.exe failure the user suffered) | Nextcloud VFS 30-40 s freezes in 360k-file folders, nextcloud/desktop#10150 still open (community-reported) | Design rules 1-9 in `systemPatterns.md`; harness from P0; supervisor thread; section 6 tests gate every release |
| R6 | Build host is Windows only | AOT cannot cross-compile from macOS | All AOT work on the user's Windows 11 x64 PC; optional GitHub Actions windows-latest; reconciler/index unit-testable on macOS behind IRemoteDrive / IPlaceholderStore |
| R7 | Sync-root indexing assumption | Only source is a CloudMirror code comment relayed in a community Q&A | Add root to crawl scope programmatically; compare Status icons inside vs outside an indexed folder in P0 step 3 |
| R8 | UX gap without thumbnails (Pictures show generic icons) | Explorer does not hydrate placeholders for thumbnails | Decide in P0 whether the user keeps photos in OneDrive; promote thumbnail COM server to late v1 if yes |

## 8. Open questions and the spike step that resolves each

| Question | Resolves in | Default if unresolved |
|---|---|---|
| Does unpackaged `StorageProviderSyncRootManager.Register` show the nav-pane node and Status icons on the user's exact build? | P0 step 3 | CfRegisterSyncRoot + registry fallback; else kill |
| Does the CsWinRT projection for net11.0-windows publish under Native AOT? | P0 step 3 | registration in a JIT helper exe, or .NET 10 |
| Has the CfUpdatePlaceholder heap corruption been fixed by a 2026 CU on this machine? | P0 step 4 | avoid CfUpdatePlaceholder |
| Does a freshly registered personal-account app hit 403 serviceReadOnly? | P0 step 1 | retry with older app / wait; else kill |
| Which `$top` does consumer delta honour and what is the real first-sync time for about 200k items? | P0 step 6 (fake), then real account in E4 | assume 200/page, 1,000+ pages |
| Is `%USERPROFILE%\NoDrive` indexed by default, and do Status icons depend on it? | P0 step 3 | always add to crawl scope |
| Where are the hydration verbs on the user's build (context menu vs provider flyout)? | P0 step 3 | tray fallbacks in E7 |
| Is Microsoft.Data.Sqlite.Core + winsqlite3 AOT-clean? | P0 step 5 | raw P/Invoke to winsqlite3 |
| Actual idle Private Bytes with one live CfConnectSyncRoot (CldApi.dll threadpool)? | P0 step 2/3 | budget 30 MB |
| Windows thread pool vs portable pool: fewer idle threads/commit? | P0 step 2 | keep AOT default |
| Does the user keep photos in OneDrive (thumbnails in v1)? | ask during P0 | thumbnails in P2 |
| Does the Range/200 fallback on downloadUrl behave for multi-GB files, and how often does downloadUrl expire mid-transfer? | P0 step 3 + E3 | refresh URL per item and on 401/403 |

## 9. How to update this plan

- This is a living document. When a step completes, a decision is taken, or a measurement lands: update the relevant row here, tick the mirrored checkbox in `progress.md`, add a line to `activeContext.md`'s session log, and add a row to the decision evolution table in `progress.md` (plus an ADR in `decisions/` for anything architectural).
- Do not delete history: strike a superseded item or move it to a "Superseded" note with the date and the reason.
- Never promote a "community-reported" or "to verify in spike" claim to a fact without recording where it was verified (spike README, OS build, date).
- Any change to section 0 or section 6 (ground rules, acceptance tests) needs the user's explicit agreement; everything else can be updated as work progresses.
- Re-read `projectbrief.md` before changing scope; scope changes go there first, then here.
