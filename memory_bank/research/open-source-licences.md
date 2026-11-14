# Open-source landscape and licence policy

Research pass of 2026-11-13 ("oss" topic, report + skeptic verdict, plus licence facts from the cfapi, graph and memory topics). Overall reliability of the oss report was rated **medium** by the skeptic; every project row below was re-checked against GitHub/NuGet on 2026-11-13 unless marked otherwise. Re-verify versions before relying on them in a later session.

Related decision: `memory_bank/decisions/ADR-0006.md` (NoDrive is MIT).

## 1. Licence policy for NoDrive

**NoDrive is MIT-licensed** (confirmed decision). Consequences:

| Source licence | What we may do | Obligations |
|---|---|---|
| MIT, BSD-2/3, 0BSD, ISC | Copy code, port to C#, modify | Keep the original copyright line and licence text in `THIRD-PARTY-NOTICES.md` (0BSD: nothing required, we credit anyway). BSD-3: no endorsement with the author's name. |
| Apache-2.0 | Copy code, port, modify | Keep copyright + licence, preserve any upstream `NOTICE` file entries, mark modified files as changed. Comes with an express patent grant; incompatible with GPL-2.0-only but fine for MIT consumers. |
| LGPL-3.0 (Dokany dokan2.dll) | Dynamically link only | User must be able to replace the library; ship LGPL text and source offer. Not needed: we use cfapi, no driver. |
| GPL-2.0-or-later, GPL-3.0, AGPL | **Read-only.** Never copy code, comments, data tables or non-trivial structure. | None if nothing is copied. Re-implement from Microsoft's published API docs. |
| EPL-1.0 (KoenZomers/OneDriveAPI) | Link only; modified EPL files stay EPL | File-level weak copyleft, GPL-incompatible. Not needed (we hand-write the Graph client). |
| No licence file (aiguoli/SimpleList), "all rights reserved" (Meziantou.OneDrive on NuGet) | **Nothing.** Do not copy. | n/a |
| WinFsp GPLv3 + FLOSS exception | Only relevant if we ever ship a mount fallback | Exception requires FLOSS licence (MIT qualifies), the WinFsp copyright line + repo link in UI and docs, and no bundling with proprietary software; otherwise GPLv3 applies or buy the 2,400/6,000 USD commercial licence. Not needed for cfapi. |

Sources: licence matrix in the oss report; https://github.com/winfsp/winfsp/blob/master/License.txt ; https://github.com/dokan-dev/dokany/blob/master/license.lgpl.txt ; https://github.com/KoenZomers/OneDriveAPI ; https://github.com/aiguoli/simplelist

**Clean-room rule for GPL code (Nextcloud, ownCloud, abraunegg, onedriver, PrimalZed, Cyberduck).** Reading it to learn the cfapi callback protocol or Graph behaviour is low risk because the protocol is Microsoft's published API. Working practice: read, write the *behaviour* down in your own words in `memory_bank/research/*.md` (as this pass did), then implement from the Microsoft docs. Never paste, never translate line by line, never carry over comments or identifier schemes. (oss report, "Clean-room note".)

**Patent and CLA notes**
- MIT/0BSD carry no patent grant; Apache-2.0 (CfSharp, Rclone UI) and GPL-3.0/LGPL-3.0 (WinFsp, Dokany, abraunegg, onedriver) carry explicit grants with termination clauses. No practical effect on inbound reuse.
- CLAs/DCO only matter if we upstream patches: Microsoft repos (CloudMirror, CsWin32, MSAL) require the Microsoft CLA; Nextcloud requires DCO sign-off; ownCloud requires a Contributor Agreement for non-trivial patches; rclone has no CLA (contributions are MIT).
- Code signing / distribution hygiene (SmartScreen on an unsigned Native AOT exe) is an open decision only if NoDrive is published; the MIT choice keeps that option open. Source: critique gap on signing, https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps

## 2. Project catalogue

Legend for *Use*: **Port** = permissive, copy/port with attribution; **Read** = copyleft or unlicensed, protocol knowledge only; **Tool** = build-time or runtime dependency. "Activity" is the last push observed on 2026-11-13.

### 2.1 OneDrive clients and sync engines

| Project | Purpose | Lang | SPDX | Latest / date | Activity | Windows / cfapi | Use for NoDrive | URL |
|---|---|---|---|---|---|---|---|---|
| rclone | Multi-provider CLI sync; `backend/onedrive` is a production Graph client; `rclone mount` via WinFsp; `bisync` two-way | Go | MIT | v1.76.0, 2026-11-06 | pushed 2026-11-12, ~61k stars | Windows yes; **no cfapi** (never pursued, see note) | **Port** protocol logic (delta, quickXorHash, chunking, restricted chars); anti-pattern reference for memory | https://github.com/rclone/rclone , https://rclone.org/onedrive/ |
| Rclone UI | Tauri/TS GUI shelling out to rclone | TypeScript | Apache-2.0 | active, 2026-11-11 | 2,403 stars | No Explorer integration | UX reference only (transfer queue pane) | https://github.com/rclone-ui/rclone-ui |
| RcloneBrowser | Qt GUI for rclone | C++ | MIT | 1.8.0, 2020-02-17 | abandoned (last push 2024-03-11) | No | None | https://github.com/kapitainsky/RcloneBrowser |
| abraunegg/onedrive | Linux/BSD OneDrive sync client | D | GPL-3.0 | v2.5.12, 2026-10-24 | pushed 2026-11-12, 13k stars | No Windows, no On-Demand | **Read**: delta processing, WebSocket change notification, SQLite item DB (schema 18 -> 19 in 2.5.12), token-in-logs hygiene | https://github.com/abraunegg/onedrive |
| jstaf/onedriver | Linux FUSE filesystem, hydrate-on-open | Go | GPL-3.0 | v0.15.0, 2026-01-31 | last push 2026-01-31, 161 open issues | No (FUSE) | **Read**: closest analogue to "hydrate on open"; inode/cache/upload-queue design | https://github.com/jstaf/onedriver |
| Nextcloud Desktop | Sync client; `src/libsync/vfs/cfapi/` is the most battle-tested third-party cfapi code | C++/Qt | GPL-2.0-or-later | v34.0.7, 2026-11-12 | very active | **Yes, cfapi** (CfRegisterSyncRoot + SyncRootManager registry keys, not WinRT) | **Read**: real callback protocol (CfExecute TRANSFER_DATA in 4 KiB-aligned blocks, CfReportProviderProgress per block, rejects FETCH_DATA from its own PID to avoid deadlock, hydration via local socket to a separate job) | https://github.com/nextcloud/desktop/tree/master/src/libsync/vfs/cfapi |
| ownCloud Desktop | Sync client; `src/plugins/vfs/win/` cfapi plugin | C++/Qt | GPL-2.0-or-later **since 2025** (previously ownCloud Commercial License) | v7.1.1, 2026-09-01 | pushed 2026-11-13 | **Yes, cfapi** | **Read**: CfOpenFileWithOplock, CfSetInSyncState, CfGetPlaceholderStateFromFindData usage; 7.1.0 (2026-06-19) made cfapi locking more granular; 2.7.3 fixed a VFS data-loss bug (cautionary) | https://github.com/owncloud/client/tree/master/src/plugins/vfs/win |
| Cyberduck | FTP/cloud browser; core libraries power Mountain Duck | Java (+C# Win UI) | GPL-3.0 | 9.6.0, 2026-11-05 | active | Windows yes; cfapi only in proprietary Mountain Duck | **Read** only; OneDrive protocol implementation | https://github.com/iterate-ch/cyberduck |
| KoenZomers/OneDriveAPI | .NET OneDrive wrapper on MSAL + Graph | C# | EPL-1.0 | v3.x, pushed 2026-08-12 | maintained | n/a | Not used (weak copyleft, depends on Graph SDK) | https://github.com/KoenZomers/OneDriveAPI |
| aiguoli/SimpleList | WinUI 3 OneDrive/GDrive manager | C# | **none** (all rights reserved) | pushed 2026-09-09 | 143 stars | No cfapi | **Do not copy** | https://github.com/aiguoli/simplelist |

Note on rclone and cfapi: forum thread 18591 (Aug-Oct 2020) was a two-person discussion with no maintainer reply; the poster concluded "no significant advantage against mount mode" and the thread auto-closed. Saying "maintainers declined cfapi" overstates it. https://forum.rclone.org/t/support-windows-cloud-files-api-and-placeholder-files/18591

### 2.2 cfapi samples, wrappers and bindings

| Project | Purpose | Lang | SPDX | Latest / date | Activity | cfapi coverage | Use for NoDrive | URL |
|---|---|---|---|---|---|---|---|---|
| Microsoft CloudMirror (Windows-classic-samples) | Canonical cfapi provider sample | C++ | MIT | rolling | maintained by Microsoft | Register/unregister, placeholders, chunked hydration with progress, custom states, thumbnails, IExplorerCommand, status UI; packaged (appxmanifest) | **Port** the structure (CloudProviderRegistrar, Placeholders, FileCopierWithProgress, ShellServices, CloudProviderSyncRootWatcher). Microsoft: "not intended to be used as production code" | https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/CloudMirror ; licence https://github.com/microsoft/Windows-classic-samples/blob/main/LICENSE |
| styletronix/cfapiSync | Bidirectional C# cfapi sync engine, "very early alpha" | C# | MIT | last push 2025-08-18 | stale, 69 stars | Sync root for current user, hydrate/dehydrate, PINNED/UNPINNED, partial population, FileSystemWatcher, retry/reconnect; `SyncProvider` + `IServerProvider` split | **Port** idioms selectively (pin-state handling, provider/server split). Known issue: Office temp-file save patterns trigger unwanted deletes. Expect to rewrite for robustness | https://github.com/styletronix/cfapiSync |
| HRAshton/WinCfApiWrapper | Read-only content source (ZIP/TAR/HTTP ranged) exposed as sync root | C# | 0BSD | NuGet 1.2.2, 2026-11-12; net8.0-windows7.0, x64 | pushed 2026-11-12, 0 stars, single maintainer | Registration/unregistration, on-demand placeholders, hydration; **no upload** | **Port** registration/hydration plumbing patterns; do not depend on the package. Bulk hydration throws AggregateException on any failure | https://github.com/HRAshton/WinCfApiWrapper ; https://www.nuget.org/packages/WinCfApiWrapper |
| MirrorPulse/CfSharp | .NET cfapi library + SQLite state store + mirror sample | C# | Apache-2.0 | previews only, no stable 1.0, no NuGet listing found; requires .NET 10 SDK, x64/ARM64 | 222 commits, active 2026, 4 stars | Packages CfSharp.Native / CfSharp / CfSharp.Storage.Sqlite: sync root, placeholders, hydration, local + remote change APIs | **Port** idioms for .NET 10-era interop and the SQLite state-store shape; not a dependency (no stable release). Facts from the cfapi report; re-check repo on first use | https://github.com/MirrorPulse/CfSharp |
| PrimalZed/CloudSync (+ fork secdev02/CloudSync) | C# port of CloudMirror on Vanara, MSIX-packaged | C# | **GPL-3.0** | pushed 2026-08-24 | 14 stars, 65 commits | Full population, basic conflict handling; README: large uploads "tend to break", no persistence, thumbnails never worked, status UI not implemented | **Read only.** Its README warns against in-proc managed shell extensions (use out-of-proc COM local servers) and lists "Convert to CsWin32?" as a TODO | https://github.com/PrimalZed/CloudSync |
| Vanara.PInvoke.CldApi | Hand-written P/Invoke for CldApi.dll | C# | MIT | **5.0.8, 2026-10-24**; TFMs up to net10.0-windows7.0 (NuGet will select it for net11.0-windows) | Vanara pushed 2026-10-24, 2.1k stars | "100% API coverage (34 of 34 functions)" per its readme (Win32Metadata lists 36 Cf* entries incl. two CfReportProviderProgress variants) | **Tool for prototyping only**: classic marshalling + SafeHandles, no AOT claim found. Its `UnitTests/.../CloudSyncProvider.cs` (MIT) wires all 13 callbacks to .NET events, a good minimal reference | https://www.nuget.org/packages/Vanara.PInvoke.CldApi ; https://github.com/dahall/Vanara ; https://raw.githubusercontent.com/dahall/Vanara/master/PInvoke/CldApi/readme.md |
| Microsoft.Windows.CsWin32 | Source generator for Win32 P/Invoke from Win32Metadata | C# | MIT | 0.3.371, 2026-11-10 | active | All 36 Cf* functions (`Windows.Win32.Storage.CloudFilters`) | **Tool, primary binding path.** AOT-safe only with `allowMarshaling=false` or `CsWin32RunAsBuildTask=true` + `DisableRuntimeMarshalling`; its built-in COM interop is **not** AOT-compatible, so any shell COM servers must use GeneratedComInterface/ComWrappers | https://github.com/microsoft/CsWin32 ; AOT notes https://github.com/microsoft/CsWin32/pull/1497/files |

### 2.3 User-mode filesystem alternatives (not chosen)

| Project | Purpose | SPDX | Latest / date | Activity | Explorer integration | Use | URL |
|---|---|---|---|---|---|---|---|
| WinFsp | User-mode FS framework with kernel FSD; native, FUSE, Cygwin and .NET APIs (`src/dotnet`) | GPL-3.0 **with FLOSS linking exception**; commercial licence 2,400 USD individual / 6,000 USD organisation (<=10 devs) per 3 years, no support | v2.1 "WinFsp 2025", 2025-06-05; betas "WinFsp 2026 Beta1-5" 2026-04-22 to 2026-10-13 | pushed 2026-11-12, 9k stars | Separate volume: no placeholders, overlays, progress or nav-pane node; two user/kernel transitions per request | Not used. Exception would apply to an MIT NoDrive only if not bundled with proprietary software | https://github.com/winfsp/winfsp ; https://winfsp.dev/com/ ; https://github.com/winfsp/winfsp/blob/master/License.txt |
| Dokany | User-mode FS driver (dokan2.sys/dokan2.dll) | LGPL-3.0 (driver, library, FUSE lib, installer); dokanctl + samples MIT | v2.3.1.1000, 2025-09-28 | pushed 2026-11-11 | None (separate volume); issue #1063 reports 2.0.x perf regressions | Not used | https://github.com/dokan-dev/dokany |
| dokan-dotnet (DokanNet) | .NET binding for Dokany | MIT | NuGet 2.3.0.3, 2025-10-22 (net8.0/netstandard2.0/net46) | pushed 2026-09-29 | n/a | Not used | https://github.com/dokan-dev/dokan-dotnet |

Why cfapi wins over both: it is built into Windows 10 1709+ (cldflt.sys minifilter, NTFS only), needs no third-party driver, no elevation, and is the only route to placeholders, Status-column icons, hydration progress and the nav-pane node. https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine . No current (2025-2026) benchmark compares WinFsp vs Dokany memory; irrelevant unless a mount fallback is ever considered.

### 2.4 Microsoft libraries we chose *not* to depend on

| Package | SPDX | Version / date | Size | AOT status | Decision | URL |
|---|---|---|---|---|---|---|
| Microsoft.Identity.Client (MSAL.NET) | MIT | 4.92.0, 2026-10-29 | 3.28 MB nupkg | Core `IsAotCompatible` since 4.77.1 (PR #5458, merged 2025-09-11) | Deferred; v1 uses hand-rolled auth-code + PKCE + system browser + DPAPI (zero deps). Possible later for WAM/SSO | https://www.nuget.org/packages/Microsoft.Identity.Client ; https://github.com/AzureAD/microsoft-authentication-library-for-dotnet |
| Microsoft.Identity.Client.Broker (+ NativeInterop 0.20.7) | MIT | 4.92.0 | 88 KB + 21.65 MB native msalruntime | Broker ships netstandard2.0/net462 only, so the AOT analyser never runs over it; cannot be bundled single-file; issue #6232 (Oct 2026, open) on loopback under PublishAot | Not in v1 | https://www.nuget.org/packages/Microsoft.Identity.Client.Broker |
| Microsoft.Graph (Kiota SDK) | MIT | 6.10.0, 2026-11-09 | 52.61 MB nupkg, pulls Kiota + IdentityModel + Azure.Core | `IsTrimmable` but **not** `IsAotCompatible`; open trimming issues (#2944); maintainers recommend custom Kiota clients for size | Not used: raw HttpClient + System.Text.Json source generators over ~10 endpoints | https://www.nuget.org/packages/Microsoft.Graph |
| OneDrive/onedrive-sdk-csharp | MIT | dead since 2018 | n/a | n/a | Not used | https://github.com/OneDrive/onedrive-sdk-csharp |

## 3. Commercial UX references (proprietary, observe only)

| Product | Model | What to copy as behaviour (not code) | Source |
|---|---|---|---|
| Mountain Duck 5 "Integrated" connect mode | Built on cfapi (Windows) / File Provider (macOS), "Mountain Duck 5 required" | The exact UX NoDrive targets: regular folder, Online Only files take no space and download on open, "Always keep on this device" / "Free up space" verbs, overlay states (Up to Date, Online Only, In Sync, Sync in Progress, Sync Error, Sync Paused, Ignored), background upload with inline progress. Limitations they document: custom mount location not honoured, file locking disabled by default. 2025 launch date not independently verified (blog not fetched). | https://docs.mountainduck.io/mountainduck/connect/integrated/ ; https://blog.cyberduck.io/2025/08/12/integrated-connect-mode/ |
| odrive | Zero-byte `.cloud` / `.cloudf` stub files, own overlay badges, nav-pane entry | **Anti-pattern**: pre-cfapi workaround, double-click downloads and replaces the stub, moving/copying stubs requires download first, poor app compatibility | https://docs.odrive.com/docs/sync-your-odrive |
| Insync | Selective sync, 1-way sync, merge folders, multi-account; no on-demand placeholders | Selective-sync UX only | https://www.insynchq.com/onedrive |
| Air Explorer | Two-pane multi-cloud transfer/backup manager, scheduling, CLI, Store listing; no Explorer integration | Transfer-queue UX only | https://www.airexplorer.net/en/back-up-and-transfer-onedrive-files/ ; https://apps.microsoft.com/detail/xp9k4zfvmn90sj |
| Cyberduck (GPL-3.0) | Open core of Mountain Duck; OneDrive protocol in Java | Read only (GPL). 9.6.0 removed deprecated OneDrive "Shared folder", fixed OAuth custom-redirect bug | https://cyberduck.io/changelog/ |

Confidence on odrive/Insync/Air Explorer details: medium (vendor pages only).

## 4. What to borrow from each permissive project, concretely

Mapped to NoDrive's hard requirement: never block Explorer, never balloon memory.

### rclone `backend/onedrive` (MIT) -> `src/NoDrive.Graph`
- **Delta handling**: `--onedrive-delta` recursive listing via Graph delta; some drives only support delta from root. Combine with Graph docs: track by id, last occurrence wins, consumer deletes omit ctag/size, 410 Gone resync. https://rclone.org/onedrive/ ; https://learn.microsoft.com/en-us/graph/api/driveitem-delta?view=graph-rest-1.0
- **quickXorHash**: rclone's implementation (native hash for Personal and Business since v1.62) is the portable reference; Graph confirms quickXorHash is the only hash guaranteed on both and that sha256Hash is unsupported; Microsoft also publishes a snippet. https://learn.microsoft.com/en-us/graph/api/resources/hashes?view=graph-rest-1.0
- **Chunking**: rclone default 10 MiB, multiple of 320 KiB (327,680 bytes), "should not exceed 250M". **Skeptic correction**: Graph requires each fragment PUT < 60 MiB, recommends 5-10 MiB, non-320 KiB multiples break the final commit; fragments sequential; no Authorization header on fragment PUTs; createUploadSession defaults `conflictBehavior` to **fail** while simple PUT defaults to **replace**, so NoDrive sets it explicitly on every write. https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0
- **Restricted characters**: Windows-reserved characters and trailing dots/spaces mapped to look-alike Unicode; limits 250 GiB per file, 400-char path, listing failures around 100k files per folder (relevant to lazy FETCH_PLACEHOLDERS batching). https://rclone.org/onedrive/
- **Auth details**: redirect `http://localhost:53682/`, refresh tokens expire after 90 days idle. **Correction**: rclone's scope list (Files.ReadWrite.All, Sites.Read.All) is business-oriented; a personal-only client needs only `Files.ReadWrite` + `offline_access`.
- **Versions/quota**: each change may create a version counting against quota; "Shared with me" not supported directly; OneNote files hidden.
- **Memory anti-patterns to avoid**: in-RAM directory trees at 0.5-1 KB per object (~1 GB per 1M objects), memory "never freed" while idle, per-open-file `--buffer-size`; rclone only spills listings to disk above `--list-cutoff` (default 1,000,000, since v1.70). NoDrive keeps the index in SQLite and sizes buffers per in-flight transfer. https://rclone.org/faq/ ; https://forum.rclone.org/t/rclone-mount-using-lot-of-ram-memory/28760
- **Global memory pool**: v1.76.0 reuses upload chunk buffers via a global pool and fixed VFS instance leaks; same idea as our bounded `ArrayPool`. https://rclone.org/changelog/
- **bisync safety**: `--max-delete` default 50%, `--conflict-resolve/--conflict-loser/--conflict-suffix`, `--resync` on first run, `--track-renames`. Source for NoDrive's mass-delete threshold and conflict-copy naming. https://rclone.org/bisync/
- **Tempauth retirement**: v1.76 notes OneDrive for Business/SharePoint pre-authenticated URLs retire 2027-04-01 (consumer scope unstated); keep the transfer layer isolated. https://mc.merill.net/message/MC1492958

### Microsoft CloudMirror (MIT) -> `src/NoDrive.Engine`
- Class split to port: `CloudProviderRegistrar` (register/unregister, nav-pane, Search crawl scope), `Placeholders` (initial creation), `FileCopierWithProgress` (chunked `CfExecute` TRANSFER_DATA + `CfReportProviderProgress`), `CloudProviderSyncRootWatcher` (local change watcher), `ShellServices` (custom states, thumbnails, IExplorerCommand, status UI; P2 and needs COM local servers).
- Its callback registration table (`CF_CALLBACK_REGISTRATION`) and `CfConnectSyncRoot` flag usage are the template; NoDrive adds `REQUIRE_PROCESS_INFO | BLOCK_SELF_IMPLICIT_HYDRATION`.
- Do **not** copy its packaged (appxmanifest) registration path or its "do work inside the callback" style; NoDrive's rule is enqueue-and-return.
- https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/CloudMirror ; https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine

### styletronix/cfapiSync (MIT)
- `SyncProvider` / `IServerProvider` abstraction (maps to NoDrive's `IRemoteDrive` / `IPlaceholderStore`), PINNED/UNPINNED attribute handling, retry/reconnect loop.
- Its known Office temp-file delete problem is a test case for NoDrive's idempotent delete handling and debounce. https://github.com/styletronix/cfapiSync

### HRAshton/WinCfApiWrapper (0BSD)
- Sync-root register/unregister sequence and `ISyncRootManager.Unregister` cleanup; on-demand placeholder creation from a content source; HTTP ranged-read hydration (matches our PROGRESSIVE policy on `downloadUrl`).
- Anti-pattern: bulk hydration throwing `AggregateException` on any failure; NoDrive fails individual requests, never the batch. https://github.com/HRAshton/WinCfApiWrapper

### MirrorPulse/CfSharp (Apache-2.0)
- .NET 10-era interop shapes and the `CfSharp.Storage.Sqlite` state-store schema as a comparison point for our SQLite index. Preserve its NOTICE if anything is copied. https://github.com/MirrorPulse/CfSharp

### Vanara (MIT) and CsWin32 (MIT)
- Vanara `CloudSyncProvider.cs` test harness: the 13-callback wiring and `StorageProviderSyncRootManager` registration call order, useful to cross-check our CsWin32 signatures. Vanara itself only for throwaway prototypes.
- CsWin32: our production binding; `NativeMethods.txt` lists only the Cf* functions, `Shell_NotifyIconW` and the message-loop functions. https://github.com/microsoft/CsWin32

### Read-only lessons from GPL projects (behaviour, never code)
- Nextcloud: reject FETCH_DATA originating from the provider's own process (deadlock), hand hydration to a separate job, 4096-byte-aligned TRANSFER_DATA blocks with the final block unaligned, progress per block, best-effort SyncRootManager registry writes. Open question whether a single-process async C# design has the same self-hydration deadlock (spike item). https://raw.githubusercontent.com/nextcloud/desktop/master/src/libsync/vfs/cfapi/cfapiwrapper.cpp
- ownCloud: granular cfapi locking (7.1.0), history of a VFS data-loss bug (2.7.3): sync correctness needs the state machine and If-Match discipline. https://github.com/owncloud/client/releases
- abraunegg: SQLite item DB with versioned schema migrations; WebSocket change notifications with polling fallback.
- onedriver: in-memory metadata cache plus on-disk content cache with "local changes win" conflict policy; read-only when offline.

## 5. Attribution checklist (`THIRD-PARTY-NOTICES.md` at repo root, per ADR-0006)

Add an entry **only when code is actually copied or ported**; reading for behaviour needs none. Keep this list in sync with the code.

- [ ] `rclone` - Copyright (C) Nick Craig-Wood and contributors, MIT. Add when quickXorHash, restricted-character mapping or chunk logic is ported. https://github.com/rclone/rclone/blob/master/COPYING
- [ ] `Windows-classic-samples / CloudMirror` - Copyright (c) Microsoft Corporation, MIT (CloudMirror README also carries an MIT notice for Fluent icons; do not ship those icons without it). Add if registrar/hydration structure is ported. https://github.com/microsoft/Windows-classic-samples/blob/main/LICENSE
- [ ] `styletronix/cfapiSync` - MIT. Add if provider/server split or pin-state code is ported.
- [ ] `HRAshton/WinCfApiWrapper` - 0BSD, attribution not required; credit voluntarily if plumbing is ported.
- [ ] `MirrorPulse/CfSharp` - Apache-2.0: copyright, licence text, upstream NOTICE entries, and a "modified" statement on changed files.
- [ ] `Vanara` - Copyright (c) David Hall, MIT. Only if any Vanara source (e.g. the test harness) is copied, or if the package ships in a build.
- [ ] `Microsoft.Windows.CsWin32` - MIT. Build-time generator with no runtime dependency; list it if generated files are committed to the repo (conservative default: list it).
- [ ] Microsoft Graph quickXorHash snippet - from Microsoft Learn; keep the page URL beside the implementation. https://learn.microsoft.com/en-us/graph/api/resources/hashes?view=graph-rest-1.0
- [ ] Never list: Nextcloud, ownCloud, abraunegg/onedrive, jstaf/onedriver, PrimalZed/CloudSync, Cyberduck (GPL, nothing copied by policy); SimpleList, Meziantou.OneDrive (unlicensed).

Also add `LICENSE` (MIT, NoDrive) at the repo root and an "Open-source notices" item in the tray About / settings page that renders `THIRD-PARTY-NOTICES.md`.

## 6. Open questions

- **ownCloud relicensing**: `vfs_win.cpp` header says "originally licensed under ownCloud Commercial License ... As of 2025, it is relicensed under GPL 2.0 or later" (SPDX GPL-2.0-or-later). Only that one file's header was checked; the exact relicensing commit/tag and whether every file in `src/plugins/vfs/win/` carries the new header are unverified. Irrelevant while we read only `master`; **never consult pre-2025 tags** of that directory. https://raw.githubusercontent.com/owncloud/client/master/src/plugins/vfs/win/vfs_win.cpp
- **Vanara under trimming / Native AOT**: no AOT claim found; classic marshalling and SafeHandles; TFMs stop at net10.0-windows7.0. If used even for prototyping on .NET 11, verify package load and IL2xxx/IL3xxx warnings. To verify in spike (only if Vanara is used at all).
- **CsWin32 on .NET 11 + AOT for function-pointer callbacks**: no published end-to-end cfapi sample under PublishAot; `[UnmanagedCallersOnly]` static callbacks with `allowMarshaling=false`. To verify in spike (P0 step 3).
- **MirrorPulse/CfSharp**: catalogue facts come from the cfapi report only (no NuGet listing found, previews only). Re-check licence file, release state and .NET 11 build on first use.
- **WinCfApiWrapper / cfapiSync viability**: 0 stars single maintainer / no commits since 2025-08-18. Both are reference code, not dependencies.
- **Mountain Duck Integrated mode launch date** (2025) not independently verified; irrelevant to licensing, relevant only as UX precedent.
- **Microsoft Q&A cfapi FAQ** statements used across this research (inconsistent CANCEL_FETCH_DATA, "headless console app can use the API", "fixed internal deadline" wording) come from external vendor moderators: **community-reported**, not Microsoft documentation. The 60 s figure itself is official (`CF_CALLBACK_TYPE` remarks: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ne-cfapi-cf_callback_type ). The moderator sample's `CF_OPERATION_ACK_DATA_FLAG_CANCELED` flag does not exist. https://learn.microsoft.com/en-us/answers/questions/2288103/cloud-file-api-faq
- **If NoDrive ever changed to GPL-3.0-or-later** it could incorporate Nextcloud/ownCloud (GPL-2.0-or-later upgrades), abraunegg, onedriver and PrimalZed code and use WinFsp under the exception. Recorded for completeness; current decision is MIT (ADR-0006).
