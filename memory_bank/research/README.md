# memory_bank/research

Verified findings from the research pass that preceded the NoDrive plan. Read these when a decision needs re-checking or when implementing against an API quirk; the plan, scope and rules themselves live in the core memory-bank files (`projectbrief.md`, `systemPatterns.md`, `techContext.md`, `bestPractices.md`) and the ADRs in `../decisions/`.

## Files

| File | Topic | Report reliability | One line |
|---|---|---|---|
| `cfapi.md` | Windows Cloud Files API | medium | Platform mechanics: process model, registration options, the 13 callbacks and the fixed 60 s deadline, hydration/population policies, CsWin32/CsWinRT interop recipe, existing implementations, 16 known pitfalls and bugs (NOTIFY_DEHYDRATE not firing, CfUpdatePlaceholder heap corruption, CfUnregisterSyncRoot deleting placeholders, ...). |
| `graph-onedrive.md` | Microsoft Graph / OneDrive personal | high | App registration and PKCE auth, delta semantics, socket.io change notifications, Range downloads, upload sessions (320 KiB chunks, conflictBehavior defaults), quickXorHash, deletes and permanentDelete, deprecations, throttling, name restrictions, why no Microsoft.Graph SDK. |
| `explorer-ux.md` | File Explorer UX | high | What Explorer renders for free, registry-based shell integration without package identity (handler-by-handler evidence), what needs identity and how a sparse package would work, Windows version notes (StatusUI 23H2+, Insider verb flyout, KB5127936), notifications, why a custom Explorer-like client was rejected, Mountain Duck / odrive references, v1 UX and ranked v2 candidates, Nextcloud risk controls. |
| `dotnet-memory.md` | Native AOT, GC, networking | high | .NET 11 GA status, Native AOT and trimming switches (what breaks, csproj starting point, CsWin32 under AOT, IsAotCompatible metadata, symbols, no cross-OS compile), GC knobs (ConserveMemory, RegionSize as runtimeconfig since .NET 10, HeapHardLimit, Aggressive collect after bursts), process patterns (headless WinExe + tray, separate settings process, Windows vs portable thread pool), SocketsHttpHandler tuning, STJ source-gen, idle discipline, published measurements and the P0 measurement plan, Avalonia 12 vs WinUI 3 for the settings process. |
| `onedrive-client-coexistence.md` | OneDrive.exe behaviour | medium (one agent, mostly community-verified registry facts) | What unlink leaves on disk (only locally available files; online-only placeholders removed, matching CfUnregisterSyncRoot), registry/SyncRootManager/CfGetSyncRootInfoByPath signals to detect a linked personal account and its folder, process locations, re-provisioning and the DisableFileSyncNGSC policy. Feeds ADR-0008. |
| `known-folder-move.md` | OneDrive "Back up folders" (Known Folder Move) | medium (Microsoft support text plus community observations) | How to turn backup off and the two per-folder choices, why backup must be turned off before unlinking, registry/API mechanics and the reliable `SHGetKnownFolderPath` detection, Microsoft's KFM limitations, third-party precedent (Dropbox, Google, FileCloud), and the design for a P2 "Folder backup" feature. Feeds ADR-0008 and E1b. |
| `open-source-licences.md` | Open-source prior art and licences | medium | Licence policy for an MIT NoDrive, project catalogue (rclone MIT, CloudMirror MIT, cfapiSync MIT, WinCfApiWrapper 0BSD, CfSharp Apache-2.0, Vanara 5.0.8 MIT; the copyleft set Nextcloud, ownCloud, abraunegg/onedrive, onedriver, PrimalZed/CloudSync GPL-3.0 as inspiration only; WinFsp/Dokany not chosen; Microsoft libraries deliberately not used), commercial UX references, what to borrow from each permissive project, attribution checklist. |

## How the research was done

- Date: **2026-11-13** (the day the plan was approved). Facts are as of that date; the session ran on macOS, so nothing was executed on Windows. Anything the documents mark **to verify in spike** is a P0 task on the Windows 11 x64 PC.
- Method: **5 topic researchers** (cfapi, graph, oss, memory, ux) each produced a report (summary, findings with claim/detail/confidence/sources, recommendations, open questions); **5 adversarial fact-checkers**, one per topic, re-verified every claim against primary sources and returned a verdict (confirmed / nuanced / refuted, with corrections and missing points); **1 completeness critic** then read everything and produced cross-topic gaps, contradictions, a recommended architecture, top risks and decisions for the user. About 690 web lookups against Microsoft Learn, NuGet, GitHub and release notes.
- The files in this folder were written from the **corrected** state: where a report and its fact-checker disagreed, the fact-checker's version is what appears. The raw reports contained the stale facts listed under "Corrections" below; do not reintroduce them.

## Evidence-quality legend (used in every file)

- **Verified** (default, no label): checked against a primary source on 2026-11-13; the URL is given next to the claim. Primary = Microsoft Learn / API reference, the project's own repository or release notes, NuGet, Windows Insider blog posts.
- **Community-reported**: Microsoft Q&A answers (these come from "Microsoft External Staff" vendor moderators such as WICLOUD Corporation, not Windows platform engineers), GitHub issues without a Microsoft reply, forums, third-party press. Treat as useful guidance, not as a Microsoft position.
- **To verify in spike**: plausible but unproven on the target machine or toolchain; each is an explicit P0 task with a kill criterion where relevant.

## Corrections the fact-checkers made (honoured in all files)

- Vanara.PInvoke.CldApi is **5.0.8 (2026-10-24)**, not 4.2.0; still not AOT-first.
- PrimalZed/CloudSync is **GPL-3.0**, not permissive: inspiration only.
- `permanentDelete` **is** supported on personal accounts (`POST /drives/{drive-id}/items/{item-id}/permanentDelete`, Files.ReadWrite).
- `/me/drive/sharedWithMe` and `/me/drive/recent` are **officially deprecated**: degraded until November 2026, then stop returning data; personal accounts affected.
- .NET 11 is **GA since 2026-11-10** (STS, support ends November 2028, same end date as .NET 10 LTS); .NET 8 and 9 left support the same day.
- Windows App SDK latest is **2.6.5 (2026-11-02)**, not 2.4.0.
- "Microsoft confirmed" statements sourced from Microsoft Q&A come from external vendor moderators and are labelled community-reported.
- `System.GC.RegionSize` / `RegionRange` are **runtimeconfig.json settings since .NET 10**, not only the `DOTNET_GCRegionSize` env var.
- `SocketsHttpHandler.ShouldEvictConnection` in .NET 11 is `[Experimental]`.
- MSAL.NET core is `IsAotCompatible` since **4.77.1**, but the WAM broker package (NativeInterop, netstandard2.0, 21 MB, not single-file bundlable) is not.
- `createUploadSession` defaults `conflictBehavior` to **fail** while simple `PUT /content` defaults to **replace**: set it explicitly on every write.
- Microsoft.Extensions.Hosting AOT status is **unverified** (and the plan avoids the Hosting/Logging stack anyway).
- `CF_OPERATION_ACK_DATA_FLAG_CANCELED` **does not exist** (a Q&A moderator invented it); fail a hydration by completing with a failing `CompletionStatus`.
- Windows 10 is not "unsupported": consumer ESU reportedly extended (community-reported) and commercial ESU runs to 2028; Windows 11-only is an effort decision.
- StatusUI flyout API was introduced in Insider build **23504**, so plan for 23H2+, not 22H2.
- The Nextcloud Explorer-freeze regression (33.0-35.0) is **not developer-confirmed as fixed** in 35.0.1.

## Critic's contradictions and how they were resolved

| Contradiction | Resolution |
|---|---|
| Hydration/population policy: cfapi report said FULL + ALWAYS_FULL; ux report said AlwaysFull *and* FETCH_PLACEHOLDERS (mutually exclusive); ux verdict preferred PROGRESSIVE | **PROGRESSIVE hydration + PARTIAL population with lazy FETCH_PLACEHOLDERS from the disk index**, mandatory at the 500k-item design target (ADR-0007). Range on `downloadUrl` is documented (206, may fall back to 200). |
| Package identity: oss report said "plan Desktop Bridge packaging early"; cfapi/ux/memory said unpackaged is sufficient | **Unpackaged v1**; identity only for Windows 11 modern context-menu entries / manifest extensions, deferred to P3 if ever. |
| Auth: graph said hand-rolled PKCE first; memory said MSAL + WAM primary; ux said MSAL public client | **Hand-rolled PKCE + system browser + loopback + DPAPI** (ADR-0003); WAM optional later (msalruntime.dll cannot be single-file bundled). |
| Windows 10 rationale | Windows 11-only kept, on effort grounds, not "Windows 10 is unsupported". |
| Library versions (Vanara 4.2.0 vs 5.0.8; 36 vs 34 Cf* functions; Windows App SDK 2.4.0 vs 2.6.5) | 5.0.8, 36 functions in Win32Metadata (Vanara's readme says 34 of 34 for its own surface), 2.6.5. |
| permanentDelete on personal | Supported (Graph v1.0 docs); default policy still "recycle bin only". |
| MSAL AOT metadata | Core declares `IsAotCompatible` (4.77.1+); Broker package does not. |
| sharedWithMe/recent deprecation | Official banners exist; no "Shared with me" UI will be built on them. |
| Thread-pool idle behaviour | Native AOT on Windows uses the Windows thread pool by default; the portable pool's 20 s retirement only applies with `UseWindowsThreadPool=false`. Measure, do not assume. |
| InSyncPolicy (data+LastWriteTime vs File/DirectoryLastWriteTime vs CloudMirror's creation time) | **LastWriteTime-based** (`FileLastWriteTime | DirectoryLastWriteTime`) so edits mark NotInSync. |
| "Microsoft confirmed" attribution | All Q&A statements downgraded to community-reported. |

## Critic's top risks (summary)

1. **cfapi platform fragility with no vendor support channel**: CfUpdatePlaceholder heap corruption on 24H2/25H2 (community-reported, open since Nov 2024), NOTIFY_DEHYDRATE not firing on "Free up space", CfDehydratePlaceholder documented only on an archived page, Explorer verbs moving into a flyout, 60 s hard deadlines that freeze Explorer when missed. Mitigation: minimise CfUpdatePlaceholder (prefer CfSetInSyncState/CfSetPinState), pin a minimum cumulative update (24H2 Aug 2025+), keep a repro suite, measure callback latency from day one.
2. **Native AOT on a days-old .NET 11 STS for interop-heavy code**: no end-to-end sample of cfapi function-pointer callbacks under AOT, CsWinRT 2.3.1 documents .NET 9/10 only, Microsoft.Data.Sqlite AOT status unofficial, no cross-OS compilation from macOS. Mitigation: spike first; fall back to .NET 10 LTS and/or framework-dependent trimmed JIT.
3. **Sync correctness and data loss**: delete propagation without guards, dehydrating edited-but-not-uploaded files, Office replace-on-save stripping reparse points, offline edits lost without USN replay, eTag races, CfUnregisterSyncRoot deleting placeholders, double-syncing alongside OneDrive.exe. Mitigation: per-item state machine, If-Match on every write, conflict copies, mass-delete threshold, never dehydrate NotInSync, uninstall flow that hydrates or warns, refuse to start while OneDrive.exe is linked to the same account.
4. **Consumer Graph uncertainty**: reported 403 `serviceReadOnly` for newly consented personal-account apps (community-reported, no Microsoft reply), unpublished consumer throttling limits, tempauth URL retirement 2027-04-01 with consumer scope unstated, sharedWithMe/recent dying Nov 2026. Mitigation: validate a fresh app + fresh account on day one, isolate the transfer layer behind an interface, honour Retry-After globally, custom User-Agent.

## Critic's gaps that shaped the plan

Build host (physical Windows 11 x64 PC confirmed), item count (200k now, 400k+ before: design for 500k with lazy population and a disk-backed index), delete policy (recycle bin only, threshold pause, InSync gate before dehydration, CopyHook on move-out), conflict model (If-Match, conflict copies, debounce, USN replay; editing deferred to P2), 60 s deadline vs Graph throttling junction (progress pings, fail fast with a status, refresh downloadUrl, tolerate 200-on-Range), AV/indexer hydration storms (concurrency cap, process-info logging, metered-network pause), coexistence with OneDrive.exe (replacement client), metadata store (SQLite via winsqlite3), sync-root indexing, thumbnails for online-only photos (Graph /thumbnails makes a COM thumbnail server cheap; late v1 if photos matter), Entra tenant prerequisite for a personal MSA, provider lifecycle (autostart, single instance, restart-on-failure, graceful disconnect), testing seams (IRemoteDrive / IPlaceholderStore, fake Graph server), code signing (personal-only vs public), moving hydration verbs (tray fallback). Each is now a decision in `projectbrief.md`, a pattern in `systemPatterns.md`, or an open question in `activeContext.md`.

## Maintenance

- When a spike or a later session settles a "to verify" item, edit the claim in place, drop the label, add the date and the evidence (screenshot path, measurement, build number).
- New durable platform knowledge (API quirks, measurements, bugs) goes into the matching topic file with a source URL; if there is no primary source, label it community-reported.
- Re-check the dated facts (package versions, deprecation dates, Insider changes) before any release; the versions here are as of 2026-11-13.
