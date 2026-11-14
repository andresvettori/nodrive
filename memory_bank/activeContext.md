# Active context

Updated at the end of every session. Read this file after `projectbrief.md` and before touching code.

## Current focus (2026-11-13)

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
