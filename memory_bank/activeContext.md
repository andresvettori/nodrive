# Active context

Updated at the end of every session. Read this file after `projectbrief.md` and before touching code. Current status and checklists are in `progress.md`; the plan is in `projectPlan.md`.

## Current focus (2026-11-16)

P1 is functionally complete and in daily use on the author's PC. The last session added the installer (WiX MSI), the lifecycle commands, the optional Switch-from-OneDrive assistant and a full documentation pass. What remains is verification and polish, then P2:

1. User checks still open: upload bandwidth limit with a real upload, Spanish text on every page, Switch assistant take-over (needs OneDrive unlinked), installer wizard wording.
2. Engineering gaps: automated responsiveness harness in CI and a 24 h soak on the AOT build, manual Explorer checklist, post-burst GC, USN reconcile, a clearer message when uninstall is refused, code signing (decision pending).
3. P2 candidates in the order I would propose: edits to existing files (today they are not uploaded), cloud thumbnails, Known Folder Move support, parallel segment download, socket.io wake-up.

## Architecture in one paragraph (as built)

One always-on Native AOT process (`NoDrive.Engine.exe`): Win32 tray + message loop; Cloud Files provider (callbacks only validate, enqueue, return; workers do the I/O); SyncService (sign-in, delta ingestion into SQLite, prefetch); RemoteApplier (mirrors cloud changes into folders on disk); UploadService (watcher + journal + adoption of identical files); LocalOps (replays Explorer deletes/renames with If-Match and the mass-delete guard); Hydrator (whole-file downloads, watchdog, shared limiter); WebUi (localhost settings and activity page, embedded HTML, EN/ES); Lifecycle (`--quit`, `--pending`, `--uninstall`, crash restart); SwitchAssistant (scan and take-over). Core and Graph stay platform neutral and testable on macOS. Details: `systemPatterns.md`.

## Dev loop and test environment

- Dev box: macOS; test PC: the user's personal Windows 11 x64 notebook (<PC_IP> over SSH from iTerm2; helper scripts in `spikes/remote/`: `mac-pc.sh`, `mac-put.sh`, `mac-sync-src.sh`, `start-in-session.ps1`; engine is started in the interactive session through a scheduled task).
- **Test the CI Native AOT exe, not a local JIT build**: freshly built unsigned DLLs can be blocked by Windows Application Control (0x800711C7). Flow: push to `main`, wait until `git fetch origin ci-binaries` shows the commit in `BUILD.txt`, copy `engine/NoDrive.Engine.exe` (or `installer/NoDrive-x64.msi`) to the PC (`C:\Users\<PC_USER>\nodrive-aot`, `Downloads`), stop the old engine (`--quit` or kill), start with `start-in-session.ps1`. The MSI can be upgraded in place with `msiexec /i ... /qn SUPPRESSLAUNCH=1`.
- The activity page token changes on every engine start; a page left open shows a stale notice. Open it again from the tray.
- Keep token use low: the user is near their budget; batch edits, avoid re-reading large files.

## Active decisions (current)

| Decision | Status | Where |
|---|---|---|
| cfapi provider, headless engine, .NET 11 Native AOT, hand-rolled PKCE, no Graph SDK, SQLite via winsqlite3, MIT | confirmed and built | ADR-0001..0006 |
| Hybrid population (index + background prefetch + on-demand) | confirmed | ADR-0007 |
| Coexistence with OneDrive.exe allowed; adoption by name + size + mtime; optional switch assistant; installer stays side by side | confirmed 2026-11-16 | ADR-0008, ADR-0013 |
| Flags 1058 without elevation; never re-register an existing root | confirmed | ADR-0009 |
| Hydration policy Full (whole file on open) | confirmed 2026-11-15 | ADR-0010 |
| Settings and activity UI = local web page served by the engine | confirmed 2026-11-15 | ADR-0011 |
| Installer = WiX v5.0.2 per-user MSI; MSIX later if signing exists | confirmed 2026-11-16 | ADR-0012 |
| v1 scope: browse, download, upload new files, delete/rename; editing existing files is P2 | confirmed | `projectbrief.md` |
| Remote deletes to the recycle bin with If-Match; mass-delete guard 200 per 10 min | built | plan E6 |

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
- **Prefers to decide at checkpoints and to test each step on the PC** ("go ahead", "let's move on"); answers with screenshots; wants every Save button prominent, previews and layouts clean, and the web page to follow Windows language (EN/ES) and regional format.
- **Optional features stay optional**: the Switch assistant never runs unless opened; the installer never touches OneDrive.

## Open questions

- Code signing certificate (Azure Trusted Signing about 10 USD/month vs none): needed before giving the MSI to other people.
- Whether to add edits-to-existing-files before or after cloud thumbnails (both P2).
- Whether the Switch assistant's take-over should get an extra rehearsal mode (dry run on a copy) before the author uses it for real.

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

### 2026-11-15 and 2026-11-16 (condensed)
- 2026-11-14 evening: first real-OneDrive mount, Flags 1058, prefetch, read-ahead (later dropped from the default path), sign-in recovery, pin detection.
- 2026-11-15: Photos "corrupted JPG" traced to Photos cancelling slow partial reads; fixed by the Full hydration policy (ADR-0010). Empty-folder population fixed (null placeholder array). E4b (mirror remote changes), E5 (upload), E6 (delete/rename), E7 (pause, autostart, crash restart) built and verified. AOT engine verified on the PC (15 MB). App icon; Explorer top navigation group is not available to third parties.
- 2026-11-15/16: local settings and activity page (EN/ES), activity journal, bandwidth limits, OneDrive status card, sticky save bar, name lookup for downloads, ShellExecute for folders.
- 2026-11-16: WiX v5 MSI + CI artifact; `--quit/--pending/--uninstall`; per-user global mutex and quit event; index rebuild; adoption of identical files (fixes reinstall duplicates); loop wake-up fix (linking from 1/s to thousands in seconds); Switch from OneDrive assistant (scan verified); full documentation update.
2026-11-16 BUG FOUND by the user after the reinstall: some folders were empty or missing files (Archivos de Microsoft Copilot Chat, AutoHotkey). Causes: (1) linking a folder that Explorer had open failed with 0x80070020 and the journal entry was dropped; (2) directories converted from plain folders were not told to fill in from the cloud (CF_CONVERT_FLAG_ENABLE_ON_DEMAND_POPULATION / CfUpdatePlaceholder ENABLE_ON_DEMAND_POPULATION), so Windows treated them as complete; (3) a fresh registration over a non-empty folder did not start adoption. Fixes: failed conversions are retried (30 s backoff), adopted folders enable on-demand population, a one-time repair walk (meta repair.v2) enables it on existing materialised folders and queues plain entries, fresh registration over existing files sets adopt.pending, and on-demand population of a folder that already has local entries creates only the missing placeholders (CfCreatePlaceholders) and completes the request empty.
