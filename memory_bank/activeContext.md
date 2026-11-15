# Active context

Updated at the end of every session. Read this file after `projectbrief.md` and before touching code.

- User insight (2026-11-14): knowing which apps behave well with online-only files improves UX a lot; recorded in `research/app-compatibility.md` and plan item E3d (Media Player is the modern Windows 11 app and still requests whole files; others still to be measured).

## Pending (2026-11-14, token-saving mode)

Engine build with read-ahead + Explorer-style pin detection (open-callback hints, in-use retries) is deployed on the PC (`nodrive-dev\engine`, log `engine6.log`). Waiting for the user's tests: video playback via Photos on an undownloaded video (see `--status`/report line for requested vs fetched bytes), keep/free on files. Next candidates: parallel segment download (single connection measured ~14 MB/s vs 300 Mbit/s line; add `--bench`), E4b apply remote changes to populated folders, E6 delete/rename propagation, E5 upload, E1b adoption.

## P1 milestone 1 delivered (2026-11-14): read-only mount of the real OneDrive

`src/NoDrive.Core` (item index, delta ingestor with resume/410 resync, population planner, hydration policy), `src/NoDrive.Graph` (GraphDrive, ThrottleGate, PKCE authenticator), `src/NoDrive.Engine` (DPAPI token store, registrar with the Flags rule, provider with bounded queues, hydrator with the P0 failure policy, populator, prefetcher, pin watcher, sync service, tray). 34 unit tests (Mac and Windows). Run on the user's PC against the real drive: sign-in via PKCE, first sync of about 91k items in 250 s at 23 MB private, folders appear in Explorer (root listing 6 ms), Camera (4,469 children) prefetched in 0.9 s.

Known gaps in M1 (all planned): read-only (delete/rename are refused; no upload), remote changes after the first sync update the index but are not pushed into folders already populated on disk (E4b), no OneDrive.exe adoption (E1b), AOT engine build not yet re-verified after the sign-in fixes, tray has no pause. Sign-in is a recoverable state: tray "Sign in..." (a slow user earlier left the first AOT build stuck on a dead listener: fixed, tested).

Dev loop on the PC: `NODRIVE_SCRATCH=<dir> spikes/remote/mac-sync-src.sh` (stop the engine first, the build overwrites its files), then `start-in-session.ps1 -Exe ...\nodrive-dev\engine\NoDrive.Engine.exe`.

## Current focus (updated 2026-11-14): P0 finished, verdict GO

Read `p0-results.md` first. Next: start P1 in the order recommended there (E1 registration with Flags 1058, then the delta/index pipeline, population, hydration with watchdog). The PC is clean (no sync roots, tasks or processes); `index500k.db` and the AOT binaries stay under `C:\Users\<PC_USER>\nodrive-dev` and `nodrive-aot` for later runs.

## Previous focus (2026-11-13)

- Plan approved; memory bank created from the plan and the 2026-11-13 research pass and reviewed for fidelity and consistency; repository scaffolded and verified: `NoDrive.slnx` with `NoDrive.Core`, `NoDrive.Graph`, `NoDrive.Engine` (net11.0-windows10.0.26100.0, PublishAot, CsWin32 NativeMethods list) and `NoDrive.Core.Tests`; `dotnet build` is clean (0 warnings, warnings-as-errors) and `dotnet test` passes 2/2 on macOS. `CLAUDE.md`, `README.md`, `LICENSE` (MIT), `.github/workflows/windows.yml` (build, test, win-x64 AOT publish) exist. Nothing has been committed to git yet (repo initialised, all files untracked). The engine's `Main` is a stub.
- Next milestone: **P0 spike** on the user's physical Windows 11 x64 PC (this planning session ran on macOS; Native AOT cannot cross-compile, https://learn.microsoft.com/dotnet/core/deploying/native-aot/cross-compile).
- The spike's job is to retire two risks before any engine code: cfapi behaviour on the user's 24H2/25H2 build, and Native AOT on .NET 11 (GA 2026-11-10, STS, support ends Nov 2028: https://devblogs.microsoft.com/dotnet/announcing-dotnet-11/).

## Recent changes

- Research pass completed: 5 topic reports (cfapi, graph, oss, memory, ux), 5 skeptic verdicts, 1 completeness critique. Corrections from the skeptics are folded into the memory bank (notably: Vanara.PInvoke.CldApi is 5.0.8; PrimalZed/CloudSync is GPL-3.0; permanentDelete works on personal accounts; sharedWithMe/recent officially deprecated after Nov 2026; Windows App SDK is 2.6.5; "Microsoft confirmed" Q&A statements are vendor-moderator, community-reported; `System.GC.RegionSize` is a runtimeconfig setting since .NET 10; `ShouldEvictConnection` is `[Experimental]`; MSAL core is AOT-compatible since 4.77.1 but the WAM broker package is not; upload-session default conflictBehavior is `fail` while simple PUT defaults to `replace`; Microsoft.Extensions.Hosting AOT status unverified; `CF_OPERATION_ACK_DATA_FLAG_CANCELED` does not exist).
- Decisions confirmed with the user (see below).
- Hard requirement added to the plan: NoDrive must never hang or block Explorer, never leave dialogs stuck, never balloon memory. Nine design rules and six measurable acceptance tests now gate every release (`projectPlan.md` sections 0 and 6, `systemPatterns.md`).

## Immediate next steps (ordered)

1. **P0 step 1: DONE 2026-11-13** (`spikes/graph-smoke`: PKCE sign-in, refresh grant, drive, delta `$top`, downloadUrl, Range 206 all verified against the real account; no 403). Full-drive enumeration measured: 136,411 items, 129 pages, 6.5 min, no throttling. Original step text: verify the registration settings (public client, `AzureADandPersonalMicrosoftAccount`, redirect `http://localhost`, scopes `Files.ReadWrite offline_access`); sign in with the real personal account via hand-rolled PKCE; call `/me/drive/root/delta`. Confirms the reported 403 `serviceReadOnly` (community-reported, https://learn.microsoft.com/en-us/answers/questions/5983388/personal-onedrive-newly-consented-apps-get-403-acc) does not bite.
2. **P0 step 2:** Native AOT memory baseline on .NET 11 (hello-world, tray-only, tray + 1 GB HttpClient download), AOT vs JIT, against OneDrive.exe and rclone mount on the same PC. Fill the measurement table in `projectPlan.md` section 2.3.
3. **P0 step 3:** unpackaged `StorageProviderSyncRootManager.Register`, placeholders from one delta page, PROGRESSIVE `FETCH_DATA` from `downloadUrl` Range requests, all under `PublishAot`; confirm nav pane, Status column, verbs on the target build.
4. Then P0 steps 4-7 (heap-corruption repro, winsqlite3 under AOT, 500k fake-server scale test, responsiveness harness), in any order.
5. Before starting code: unlink OneDrive.exe from the account on the test PC (NoDrive refuses to start while it is linked), and note the exact OS build and cumulative update in the spike README.
6. Set up the Windows PC: .NET 11 SDK (already installed), Visual Studio 2022+ "Desktop development with C++" workload for Native AOT (https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/), enable the `Microsoft-Windows-CloudFiles/Operational` event log.

## Active decisions and status

| Decision | Status | Notes |
|---|---|---|
| cfapi provider, not a custom Explorer-like client | confirmed | ADR-0001; explorer.exe renders everything, engine stays headless |
| .NET 11 + Native AOT first; fall back to .NET 10 LTS AOT, then trimmed JIT | confirmed, gated by P0 | ADR-0002; kill criteria in `projectPlan.md` 2.4 |
| Hand-rolled auth-code + PKCE + system browser + loopback + DPAPI; MSAL/WAM deferred | confirmed | ADR-0003; WAM broker package not AOT-analysed and not single-file bundlable |
| No Microsoft.Graph SDK; HttpClient + System.Text.Json source generators, about 10 endpoints | confirmed | ADR-0004 |
| Index in SQLite via system winsqlite3.dll; fallback raw P/Invoke, then custom binary store | confirmed, verify in P0 step 5 | ADR-0005 |
| MIT licence; permissive sources only, GPL projects read for protocol knowledge | confirmed | ADR-0006 |
| PopulationPolicy PARTIAL with lazy FETCH_PLACEHOLDERS from the index; design for 500k items | confirmed | ADR-0007; never pre-create hundreds of thousands of placeholders |
| v1 scope = browse, download, upload new files; in-place editing in P2 | confirmed | |
| NoDrive replaces OneDrive.exe for the account; refuse to start while OneDrive.exe is linked | confirmed | |
| Unpackaged in v1; no package identity; registry-based shell integration | confirmed | sparse package only in P3 if modern context-menu entries are wanted |
| HydrationPolicy PROGRESSIVE, AUTO_DEHYDRATION_ALLOWED, InSync on LastWriteTime | default, verify Range behaviour in P0 step 3 | |
| Windows 11 only, 24H2 with Aug 2025 CU or later | default | Windows 10 22H2 under ESU shares the API baseline but is untested |
| Remote deletes go to recycle bin only; mass-delete threshold pauses sync | default | permanentDelete exists on personal but is never the default |
| Thumbnails in P2 unless the user keeps photos in OneDrive | open, ask during P0 | |
| OneDrive.exe guard (refuse while personal account linked), former-folder auto-detection, adoption of existing files in v1 (E1b) | confirmed 2026-11-13 | ADR-0008; unlink leaves only ordinary files, so adoption is metadata-only; KFM-redirected folders excluded in v1 |
| Change wake-up: adaptive delta polling in v1, socket.io in P2 | default | |
| Settings UI: tray + browser sign-in + JSON config in v1; real window (Avalonia 12 or WinUI 3) later | default | |

## Important patterns and preferences (about the user)

- **Low-memory obsession.** OneDrive.exe took almost 4 GB on an 8 GB PC with many files. Idle memory must be a fixed budget independent of item count, and memory must come back after each job. Report Private Bytes/commit, not Working Set.
- **Explorer must never hang.** The user lived through constant Explorer freezes and move dialogs stuck at "0 files remaining" with the folder already moved. Any work inside a cfapi callback other than validate-and-enqueue is a bug. Delete/rename acks never wait on the network.
- **Dislikes bloat.** Likes rclone's resource use, rejects its multi-provider surface and console/admin UX. One provider, one account, minimal dependencies, no UI framework in the resident process, no Microsoft.Graph SDK, no MSAL in v1.
- **Wants a Cline-style memory bank** (https://docs.cline.bot/best-practices/memory-bank) read at the start of every session and updated after every significant change or on "update memory bank".
- **Wants concise technical communication:** experienced C# developer; headings, bullets, numbers, sources; no filler, no emojis.
- **Facts must be sourced.** Claims carry a URL or are labelled community-reported / to verify in spike. The user has been burned by vendor-moderator "confirmations".
- **Uses OneDrive "Back up folders"** (Desktop, Documents, Pictures redirected into OneDrive). Migration must turn backup off before unlinking; native folder-backup support is the first P2 item (`research/known-folder-move.md`).
- The Windows 11 x64 PC is the user's **personal notebook**, not a company-managed device.
- Has .NET 11 installed and a physical Windows 11 x64 PC for building and testing; this planning session ran on macOS.

## Open questions

See `projectPlan.md` section 8 for the full table with the resolving spike step. The ones that can kill the project:
- Unpackaged registration showing nav pane and Status column on the user's build (P0 step 3).
- CsWinRT projection and cfapi function-pointer callbacks under Native AOT on net11.0 (P0 step 3).
- Fresh personal-account app getting 403 serviceReadOnly (P0 step 1).

Non-blocking: CfUpdatePlaceholder heap corruption status (step 4), winsqlite3 AOT (step 5), consumer delta `$top` and real first-sync time (step 6), verb placement and indexing of the sync root (step 3), photos/thumbnails (ask).

## Session log

### 2026-11-14
- First run on the user's Windows 11 PC (26H2 build 26300.9974, OneDrive.exe running): `onedrive-state.ps1` shows folder backup ON for Desktop/Documentos/Fotos (localised names); `aot-baseline --variant tray` idles at **4.3 MB private / 14.5 MB WS** for 20 min; `cfapi-minimal` registered via WinRT under AOT, connected, created 200 placeholders, idle **10.6 MB private**. No Explorer interaction happened, so hydration/acks are still untested.
- Feedback from the user: say explicitly which commands block and how to stop them. Runbook, README and both spikes' start-up text now state it.
- Spike hardened for re-runs: detects its own registration via the SyncRootManager registry key, skips existing placeholders, prints a hint if no callback arrives in 2 minutes.
- **Remote loop working (2026-11-14):** the PC runs OpenSSH (key-only, Private profile). VS Code's shell lacks macOS Local Network permission ("No route to host"), so commands go through iTerm2: `spikes/remote/mac-pc.sh '<powershell>'` and `mac-put.sh <local> <C:/remote>` (set `NODRIVE_SCRATCH`, `NODRIVE_PC`). `git pull` on the PC cannot authenticate over SSH (HTTPS credential), so changed files are pushed with `mac-put.sh`. The PC has the .NET 11 SDK but no C++ build tools, so it builds JIT only (`dotnet publish ... -p:PublishAot=false`); AOT is verified by CI. Spikes start in the interactive session via `start-in-session.ps1`.
- **Key finding:** Windows requests the whole file at open despite PROGRESSIVE (see research/cfapi.md). Provider-down open fails fast (17 ms, 0x80070194); cleanup deletes unhydrated placeholders (164 of 171) as documented; hydration memory stays flat.
- **P0 step 4 done:** CfUpdatePlaceholder heap corruption not reproduced (14,000 calls); the PC no longer sleeps on power (user changed the setting), which had been dropping scp transfers. Remaining: step 7 (fault injection, responsiveness under load, kill/restart) and the written go/no-go.
- **Prefetch policy defined (2026-11-14):** by cost not depth: heavy folders only (children >= about 2,000 = 300 ms at 160 us each), with their ancestor chain; reactive along the current branch (heavy children only); whole-drive fill only within a budget; giant folders always early. Real drive: 1 folder >= 2,000 children (Camera, 4,469 -> 0.7 s), whole drive 15 s of Windows time and about 55 MB (0.6 KB/placeholder). The real drive is now 95,435 items (user tidied it). See projectPlan E3c.
- **Explorer busy-cursor problem found by the user and fixed in the spike (2026-11-14):** opening the 31k folder cold froze the window 7-10 s; batch size/parallelism cannot help (Windows creates placeholders at about 160 us each); background prefetch + `CfUpdatePlaceholder(DISABLE_ON_DEMAND_POPULATION)` makes the open take 1.1 s with no callbacks and does not slow foreground listing. The PC is currently running the provider with the big folder PREFETCHED; the user should confirm in Explorer that `Reunión 7` opens without the freeze. Design item E3c added.
- **P0 step 6 cfapi half measured (2026-11-14):** `cfapi-minimal --index` answers FETCH_PLACEHOLDERS from the SQLite index (population FULL; WinRT has no Partial): 31,030 children in 6.5 s, callbacks 0.1 ms, +2.5 MB memory. The PC is reset (fresh registration, folders unopened) waiting for the user to open `NoDriveSpike\Reunión 7` in Explorer. CI failure explained: two overlapping runs raced on the ci-binaries force-push (`cannot lock ref`); workflow now cancels superseded runs and retries the push.
- **P0 steps 5 and 6 (index half) done (2026-11-14):** SQLite via winsqlite3 is Native-AOT clean; 500k items ingest at 33 MB peak / 10.3 MB settled; listing a 30k-child folder 153 ms; resume exact; memory independent of item count. Next: wire the cfapi spike to this index (PARTIAL population, FETCH_PLACEHOLDERS from the DB) and open the 30k-child folder in Explorer; then the responsiveness/fault harness (step 7) and step 4, then go/no-go.
- **P0 step 2 measured (2026-11-14, Native AOT, user's PC):** tray idle 4.3 MB; 1 GB HttpClient burst peak 9.0 MB, 6.4 MB after release GC; cfapi provider idle ~13-15 MB and +2.6 MB during a 1.68 GB pin hydration; OneDrive.exe idle 209 MB private. AOT cfapi build verified functionally on the PC. PC cleaned (spike unregistered, folder removed, no tasks left). Remaining P0: step 4 (heap corruption repro), 5 (SQLite under AOT), 6 (500k scale), 7 (responsiveness harness), then go/no-go.
- **ADR-0009 simplified (2026-11-14):** `BUILTIN\Users` can write under SyncRootManager, and a medium-integrity (UAC-filtered) process wrote Flags successfully, so no elevation/UAC is needed for the verbs; the engine registers once and applies/repairs Flags itself. Native AOT `cfapi-minimal` (from CI branch `ci-binaries`) now runs on the PC.
- **Pin/unpin works (2026-11-14):** the spike's `PinWatcher` hydrates on pin (2.4 s for 1.3 MB) and dehydrates on unpin (17 ms). A pin made while the watcher was not running is not re-signalled, so the engine needs a start-up sweep.
- **Explorer integration solved (user-confirmed):** Flags=1058 gives Status column, standard icons and verbs identical to OneDrive; no handler or package identity needed. P0 step 3 is a pass. Next: run the AOT binaries on the PC, the 1 GB burst and OneDrive reference rows (step 2), SQLite under AOT (5), then 500k scale test (6) and responsiveness harness (7), then go/no-go.
- **Decision (user, 2026-11-14): take the elevated-setup route for the verbs** (ADR-0009). Found: re-running `Register` resets Flags to 34, so the engine must register once at setup and only connect at start-up. Status column / brown icon still unsolved at 1058 (pending the user's observation).
- **Flags experiments:** verbs appear with Flags=1058 (OneDrive's value) and not with 34, 2 or 1026; Status column shows with Flags=2 (and the brown icons vanish) but is blank with 34. No API property sets 0x400 (probe). Open: what Status/icons look like at 1058; how to set 0x400 without admin or package identity. The running spike's key is currently hand-set to 1058.
- **Shell-property check (2026-11-14):** our placeholders and OneDrive's online-only files report identical `System.FilePlaceholderStatus=8`, `StorageProviderState=1`, attributes and reparse tag; Windows Search was NOT the cause (OneDrive's Status column works with Search disabled); `AllowPinning` and `Flags` are not the cause (34 = Nextcloud's "full" value; do not clear 0x20). The missing verbs/Status/icons need shell presentation handlers (CustomStateHandler) or package identity. Decision pending with the user.
- Explorer manual test done by the user: responsive, no hangs, open/rename/delete fine. **Missing: "Always keep / Free up space" verbs and Status column content.** Leading hypothesis: Windows Search service is Disabled on the PC (cloud state flows through the indexer); second: missing shell handlers in our SyncRootManager key. Experiment: enable WSearch (needs an elevated shell) and re-check; compare with the OneDrive folder's behaviour on the same PC.
- Next on the PC: the Explorer sequence in runbook F.4 (nav pane, Status column, double-click a small file, pin, rename, delete), then the provider-kill test and `--cleanup`; then step 2 burst and reference rows.

### 2026-11-13
- Research pass (cfapi, graph, oss, memory, ux) with skeptic verdicts and completeness critique completed; corrections honoured.
- Plan reviewed and approved by the user; decisions confirmed: v1 scope browse/download/upload-new, OneDrive.exe replacement, 500k-item design target, MIT, unpackaged, hand-rolled PKCE, no Graph SDK.
- Hard requirement added: never hang Explorer, never balloon memory; nine design rules and measurable acceptance tests.
- Memory bank created (`projectbrief.md`, `productContext.md`, `systemPatterns.md`, `techContext.md`, `activeContext.md`, `progress.md`, `projectPlan.md`, `bestPractices.md`, `research/`, `decisions/`) by 9 writers and fixed by 2 reviewers (fidelity to sources, cross-file consistency).
- Repo scaffolded and verified on macOS: solution, 4 projects, CI workflow, root configs; build 0 warnings, 2/2 tests pass. Toolchain quirks recorded in `techContext.md` (slnx by hand, MTP opt-in in global.json, CsWin32PlatformTarget x64). Engine `Main` is a stub; nothing is committed yet.
- README rewritten with build/test/publish (now) and configure/run/coexistence/uninstall (planned) sections.
- Researched OneDrive.exe unlink behaviour and detection surface; wrote ADR-0008 and `research/onedrive-client-coexistence.md`; added epic E1b (guard, folder detection, adoption). User confirmed E1b (adoption) in v1 scope; ADR-0008 Accepted.
- Clarified the Entra registration model in README and ADR-0003: publisher registers once, client ID is public and embedded with a `config.json` override; users never register.
- User confirmed they use folder backup (KFM). Researched stop-backup behaviour, registry/API mechanics and third-party precedent; wrote `research/known-folder-move.md`; migration procedure added to ADR-0008 and README; detection design in systemPatterns 8.1/E1b; "Folder backup support" added as the first P2 item.
- User created the Entra app registration (client ID 6711e7c6-ec09-42df-a5bb-bfb449119fdc); embedded as the default in NoDrive.Graph, recorded in techContext and README.
- Built and ran `spikes/graph-smoke` on macOS: P0 step 1 passed (no 403, PKCE and refresh work, `$top=1000` gives 1019-1201 items/page, downloadUrl needs a plain item GET, Range 206 at arbitrary offsets, sha1/sha256/quickXorHash all present). Facts recorded in `research/graph-onedrive.md`. Refresh tokens rotate on every refresh: the engine must persist the newest one.
- Full-drive delta measured: 136,411 items (17,286 folders, 119,125 files), 129 pages, 388.7 s, avg 3.0 s/page, 88 MiB JSON, no 429. The drive is smaller than the 200k the user estimated; the 500k design target stands.
- Wrote `p0-runbook.md` (now fully PowerShell: PC preparation with winget, publish/run/measure/cleanup commands and manual checks for steps 2-8), `spikes/measure.ps1`, and `spikes/onedrive-state.ps1` (read-only OneDrive/KFM/sync-root/policy report).
- Wrote the P0 step 2 and 3 spikes (`spikes/aot-baseline`, `spikes/cfapi-minimal`) plus shared `spikes/Shared/{TrayWindow,ProcessStats,SpikeAuth}.cs`; all compile on macOS against the CsWin32 bindings (allowMarshaling=false, UnmanagedCallersOnly callbacks) but have never executed. First Windows run will find the runtime mistakes.
- User does not see "Back up folders" in OneDrive settings. The Windows PC is their **personal notebook** (not company-managed), so the likely causes are the relocated control (Settings > Sync and backup > Manage backup) or backup being off; `onedrive-state.ps1` decides. Documented in README, research/known-folder-move.md 1b and ADR-0008.
- Burst-test input: `graph-smoke --largest N` lists the biggest files by item id; `aot-baseline --variant http --item <id>` fetches a fresh downloadUrl itself (URLs expire in about an hour, so none is hard-coded in docs). Candidate ids recorded in `p0-runbook.md` step 2.3.
- Repo pushed to the private GitHub repo `git@github.com:andresvettori/nodrive.git` (branch `main`, commits: initial LICENSE/.gitignore, scaffold+memory bank+spikes, merge). The `windows.yml` workflow should run on this push; check the Actions tab for the first win-x64 AOT publish result.
- CI run #2 green: engine Native AOT publish works on win-x64/.NET 11 (toolchain risk partially retired). Workflow bumped to checkout@v7 / setup-dotnet@v6 / upload-artifact@v7 (Node 20 deprecation) and extended to AOT-publish both spikes with IL2/IL3 warning report; artifact `spikes-win-x64` is downloadable from the Actions run.
- Runbook and README now have a "fast path": download the `spikes-win-x64` CI artifact (exes + scripts + runbook copy), `Unblock-File`, run `onedrive-state.ps1`, `aot-baseline`, `cfapi-minimal` with no SDK or build tools on the PC.
- CI run #5 surfaced that Native AOT ignores `runtimeconfig.template.json`; GC settings moved to `RuntimeHostConfigurationOption` items in `eng/LowFootprint.props` (shared by engine and spikes). Without this, step 2 memory numbers would have been measured with default GC settings.
- CI run #5 green with zero AOT warnings for both spikes (cfapi-minimal 6.06 MB): WinRT projection + callbacks compile under Native AOT on .NET 11. Download artifacts from run #6+ (GC fix).
- First PC run done 2026-11-14 (see session log): tray idle 4.3 MB, cfapi registration + 200 placeholders OK under AOT; hydration not yet exercised.
- Next: on the notebook `git clone git@github.com:andresvettori/nodrive.git` (or HTTPS), run `.\spikes\onedrive-state.ps1`, then runbook steps 2 and 3; expect to iterate on the cfapi spike with real callback behaviour.

## 2026-11-14 decision
Video playback problem deferred by the user; moving on with E4b (remote changes into populated folders), then E6 delete/rename, E5 upload. See progress.md "Known issue".
E4b implemented (2026-11-14): `RemoteApplier` (Engine/CloudFiles) mirrors delta changes into populated folders: create placeholder, rename/move, delete (placeholders only, never real local files), update size/time + dehydrate via CfUpdatePlaceholder (file busy: logged, skipped). Hooked through `DeltaIngestor.BeforePage`; skipped during first enumeration and 410 full resync. NOTIFY_DELETE/RENAME from our own process are acked success; users stay read-only. Untested on the PC until the user changes something on onedrive.com. Known gaps: orphaned child rows if Graph omits child deletions; busy-file updates are not retried; stale placeholders after a full resync.
E4b verified on the PC (2026-11-14): remote create/rename/delete appear in populated folders; Explorer refreshes without F5 via SHCNE_UPDATEDIR. Next: E5 upload of new files, then E6 delete/rename from Explorer.
E5 implemented (2026-11-14), untested on the PC: `UploadService` (Engine/CloudFiles) + `GraphDrive.UploadNewFileAsync/CreateFolderAsync` + `uploads` journal table (schema v2) in ItemIndex. Watcher records new real (non-reparse) files/folders, 5 s quiet period, 2 lanes, files <= 4 MiB single PUT else 5 MiB chunked session, conflictBehavior=fail with "(NoDrive conflict ...)" copy on 409, then CfConvertToPlaceholder(MARK_IN_SYNC). Startup scan of populated folders queues files added while the engine was off. Edits to existing files are NOT uploaded (P2). Delete/rename from Explorer still refused (E6).
E5 verified on the PC (2026-11-14). Next: E6 delete/rename from Explorer.
E6 implemented (2026-11-14), untested on the PC: delete/rename in Explorer are acked at once (success) and posted to `LocalOps` (Engine/CloudFiles), journalled in the `remote_ops` table (schema v3), replayed in order with backoff. Delete = Graph DELETE with If-Match (recycle bin; 412 keeps the file and the delta restores it); rename/move = PATCH with conflictBehavior=fail then a "(NoDrive conflict ...)" name; rename to outside the sync root (Recycle Bin) = delete. Mass-delete guard: 200 deletions per 10 min, then held until tray "Allow N held deletions...". RemoteApplier skips items with a pending op so a deleted file is not recreated. Own-process deletes/renames (RemoteApplier) are acked but never posted.
2026-11-15: Photos "corrupted JPG" solved by the Full hydration policy (ADR-0010). Also fixed empty-folder population (null placeholder array). Open check: Explorer thumbnails under Full (bandwidth, memory); update techContext/systemPatterns wording that still says PROGRESSIVE.
