# Research: Windows Cloud Files API (cfapi)

Verified knowledge base from the 2026-11-13 research pass (cfapi topic report + adversarial verdict, plus cfapi-relevant critique gaps). Overall reliability of the underlying report was rated **medium**: the Win32/WinRT reference facts are solid, but most bug reports and "Microsoft confirmed" statements come from Microsoft Q&A answered by **vendor moderators ("Microsoft External Staff", e.g. WICLOUD Corporation), not Windows engineers**, and are labelled *community-reported* here. Anything labelled *to verify in spike* is a P0 task.

Why this document matters for NoDrive: the user's OneDrive.exe experience (Explorer hangs, move dialogs stuck at "0 files remaining", ~4 GB RAM) is exactly what a cfapi provider produces when it does slow or blocking work inside a callback, misses the 60 s deadline, or keeps the item tree in RAM. Every rule below that touches callbacks, deadlines or acks is a hard requirement, not a nicety.

Related: `memory_bank/systemPatterns.md` (how we apply this), `memory_bank/bestPractices.md` (callback rules), `memory_bank/decisions/ADR-0001` (cfapi over custom client).

---

## Explorer manual test on the user's PC (2026-11-14, user drove Explorer, provider log read over SSH)

User report: Explorer always responsive, nothing hung; renames and deletes instant; all files opened and displayed correctly and quickly. NOT working: (1) no "Always keep on this device" / "Free up space" anywhere, for files or folders (even under "Show more options"); (2) no Status column content (three "Status" columns offered, all blank); online-only files show a small brown box icon, hydrated files and folders show no icon.

Findings from the provider log during that session (54 hydrate requests):
- **Cloud-aware callers get real progressive ranges.** `Photos.exe` requested small ranges (e.g. `required=[69632+61440] optional=[131072+5121595]`, sizes 4-64 KiB), one callback per range. `explorer.exe` (thumbnail/preview) requested a whole small file. **`Microsoft.Media.Player.exe` requested `required=[0+1492094132]` (the entire 1.4 GB mp4) at open and the hydration took 97.9 s**, i.e. media playback of a never-hydrated large video waits for the full download (or at least holds the whole range as "required"). This mirrors the earlier PowerShell/Win32 result and the known memory-mapped-open pitfall; it is not specific to scripts.
- **Per-request cost in the spike was about 1.5 s** for 4-64 KiB ranges because the spike creates a new `HttpClient` (new TLS connection) per hydration. Engine design: one shared pooled client per host and use the `optional` range to read ahead.
- **Races are survivable:** deleting/renaming a file mid-hydration produced `CfExecute(TRANSFER_DATA)` errors 0x80070178 ("The file is not a cloud file") and 0x8007018E ("cloud operation was canceled by user"); the provider failed those requests cleanly (status 0xC000CF11) and kept running.
- Callbacks seen: `NOTIFY_RENAME`, `NOTIFY_DELETE` including `CF_CALLBACK_DELETE_FLAG_IS_UNDELETE` (Explorer's delete-then-undo path) and repeated deletes of the same item (idempotent handling confirmed necessary).

Why the Status column and verbs may be missing (hypothesis under test): **Windows Search is Disabled on this PC** (`WSearch` StartType Disabled, SearchIndexer not running; index query fails with 0x80040E14). Cloud-file state shown in Explorer flows through the property system / indexer, and Microsoft's CloudMirror sample states property queries return wrong values when the sync root is not indexed. Registry comparison: our key (written by `StorageProviderSyncRootManager.Register`) has `NamespaceCLSID`, `DisplayNameResource`, `IconResource`, `Flags=34` and `UserSyncRoots`; OneDrive's key has `Flags=1058` plus `Handler`, `CustomStateHandler`, `ThumbnailProvider`, `UriHandler`, `ShareHandler`, `CopyHook`, `SearchHandlerFactory`, `StorageProviderStatusUISourceFactory`, `ProtectionMode`, `Cid`, `AUMID`. The built-in verbs and Status column are not documented to depend on those handlers, but the handler list is the second hypothesis (richer shell integration needs registered handlers/package identity).

## SyncRootManager `Flags` experiments on the user's PC (2026-11-14, SSH session is fully elevated so Claude can write our own key)

Hand-edited `Flags` on the spike's own key, user checking Explorer (reopened window / restarted Explorer):

| Flags | Bits | Status column | Overlay icon | "Always keep on this device" / "Free up space" |
|---|---|---|---|---|
| 34 (what `Register` writes) | 0x20 + 0x02 | blank | generic brown box on online-only files | **absent** |
| 2 | 0x02 | **shows** (files and folders) | gone | absent |
| 1026 | 0x400 + 0x02 | as for 2 | as for 2 | absent |
| 1058 (= OneDrive's value) | 0x400 + 0x20 + 0x02 | **shows, identical to OneDrive's** (user, 2026-11-14) | **standard cloud/check icons, identical to OneDrive's** | **PRESENT** |

User also observed (2026-11-14, Flags=1058) that double-clicking an online-only file made **Windows itself show the native toast "<provider> is automatically downloading the file from ..."**, with no notification code in NoDrive: the hydration toast and progress are part of the platform integration unlocked by the flags (the earlier Windows-App-SDK toast question for the engine is therefore moot for downloads).

Conclusions: **Flags=1058 alone gives the complete native experience** (verbs, Status column, standard cloud icons) without any custom COM handler or package identity; the blank Status and brown box seen with 34 are what Explorer shows when 0x20 is set but 0x400 is not. The verbs need both 0x20 and 0x400; with 0x20 cleared (Flags 2) the plain Status works but there are no verbs. The research inference "0x20 enables the verbs" was incomplete (0x20 alone is not enough), and "0x20 hides the menu" was wrong.

Probe of the WinRT API (`cfapi-minimal --probe-flags`, throwaway roots): `Register` writes Flags=34 for baseline, `AllowPinning`, `ProtectionMode` (also writes a `ProtectionMode` registry value), `RecycleBinUri` (writes `RecycleBinUrl`), and item property definitions; **`ShowSiblingsAsGroup=true` adds 0x200 (Flags=546)**. **No `StorageProviderSyncRootInfo` property produces 0x400**, so getting verbs from an unpackaged app means writing `Flags` in HKLM ourselves (administrator rights: an installer step, and must survive re-registration) or finding the registration route that sets it (package identity / `windows.cloudFiles` manifest extension is the likely one).

## Why the verbs / Status column / icons are missing for an unpackaged provider (2026-11-14, user's PC + research)

Measured:
- OneDrive's own folder shows the Status column and the verbs on the same PC **with Windows Search disabled**, so the indexer is not the cause (hypothesis rejected).
- `StorageProviderSyncRootInfo.AllowPinning = true` changed neither the registry `Flags` nor the menu.
- **The shell sees our placeholders exactly like OneDrive's online-only files:** `System.FilePlaceholderStatus = 8`, `System.StorageProviderState = 1`, file attributes `0x401620` (RECALL_ON_DATA_ACCESS | OFFLINE | ARCHIVE | ...) and reparse tag `0x9000401a` (`IO_REPARSE_TAG_CLOUD`, "FeRp" data) are identical for both. The platform side of cloud files is therefore correct; the missing pieces are shell *presentation* components.
- Registry: our `SyncRootManager` key (from `Register`) = `NamespaceCLSID`, `DisplayNameResource`, `IconResource`, `Flags=34`, `UserSyncRoots`. OneDrive's adds `Flags=1058` and 10 COM handler CLSIDs.

Research (report 2026-11-14, evidence grades A doc / B shipping code / C community):
- `Flags` is undocumented. Nextcloud (B, `cfapiwrapper.cpp`) uses `syncRootFlagsFull = 34` and `syncRootFlagsNoCfApiContextMenu = 2`, so **0x20 enables, not hides, the built-in cfapi context menu**; our 34 is already the "full" value. (This corrects the earlier note that 0x20 hides the menu; do NOT clear it.) The meaning of OneDrive's extra 0x400 is unknown.
- Nextcloud (unpackaged) additionally writes `CustomStateHandler` and `ThumbnailProvider` CLSIDs under the SyncRootManager key and registers its own COM shell-extension DLL under `HKCU\Software\Classes\CLSID` (DllSurrogate AppID); its comments say these are "needed to make the progress bar refresh correctly and also add status icons". It registers with `CF_REGISTER_FLAG_UPDATE`, hydration FULL, population PARTIAL, `InSync = PRESERVE_INSYNC_FOR_SYNC_ENGINE`, creates placeholders `MARK_IN_SYNC` and uses pin states `UNPINNED`/`INHERIT`. Nextcloud forum users report seeing "Free up space" (C).
- The Status column and custom icons very likely come from `CustomStateHandler` (`IStorageProviderItemPropertySource`) + `StorageProviderItemPropertyDefinitions`; the generic brown-box icon is probably the shell failing to resolve a state icon (C, inference). Microsoft's doc says registration alone "automatically provides several verbs" (A) but also that sync engines are designed to use the Desktop Bridge / package identity (A); the CloudMirror sample is MSIX-packaged.
- Observed behaviour (C): folder-level "Free up space" appears only if a descendant is pinned; a pure online-only file should offer "Always keep on this device".

Ranked experiments: (1) register and implement a `CustomStateHandler` COM server (and `StorageProviderItemPropertyDefinitions`), Nextcloud style; (2) confirm handler CLSIDs resolve under HKCU; (3) check `IconResource` resolves; (4) set `CF_PIN_STATE_UNPINNED`/`INHERIT` on new placeholders and compare register policies with Nextcloud; (5) `StorageProviderStatusUISourceFactory` via registry (Win11 23H2+ flyout only); (6) give the app **package identity** (sparse package with the `windows.cloudFiles` extension) as the officially supported route; (7) last resort, hand-edit Flags (unknown 0x400).

## Hydration, acks, provider-down and cleanup, measured over SSH (2026-11-14, same PC, JIT build of `cfapi-minimal`, OneDrive.exe running)

Driven from the Mac over SSH (reads/renames/deletes issued from a non-interactive session; the provider ran in the interactive session 1):

| Test | Result |
|---|---|
| Open + read a 14 KB online-only PNG | Intact (PNG header ok), 2.1 s total. Request logged: `required=[0+14741]`, caller `powershell.exe` (CF_PROCESS_INFO works). |
| Rename a placeholder | `NOTIFY_RENAME` received with correct source/target paths and flags `SOURCE_IN_SCOPE | TARGET_IN_SCOPE`, acked inside the callback; **8 ms** for the caller. |
| Delete a placeholder | `NOTIFY_DELETE` received, acked in the callback; **8 ms** for the caller. |
| Hydrate a 1.7 GB / 1.4 GB / 0.9 GB / 0.6 GB file | All completed correctly. Throughput about 12.5-15 MB/s on the user's Wi-Fi (1676.8 MiB in 134 s; 572 MiB in 38 s). **Spike private bytes stayed 16-21 MB** (JIT build) through a 1.7 GB hydration: streaming into `CfExecute(TRANSFER_DATA)` with one pooled 1 MiB buffer keeps memory flat. Progress reported every chunk; no 60 s timeout despite 134 s hydrations. |
| **Required range with PROGRESSIVE policy** | **Every open requested `required=[0+<whole file size>] optional=[0+0] flags=0x0`**, for reads at offset 0, at the middle, with `FileOptions.RandomAccess`, and via raw Win32 `CreateFile`/`ReadFile`. The platform recorded the policy we asked for (`CfGetSyncRootInfoByPath`: hydration=PROGRESSIVE/AUTO_DEHYDRATION_ALLOWED, population=ALWAYS_FULL). |
| **Where the caller blocks** | In **`CreateFile` (the open)**, not in the read: a raw `CreateFile` took the whole hydration time (about 42 s for 572 MB) and the following `ReadFile` at the middle took 0.0 s. A .NET `File.Open` + 4 KB read returned after 38.1 s = exactly the hydration time of that file. Effective behaviour on this build is therefore **FULL-like: the opener waits until the entire file is local**, whatever the app reads. |
| Provider stopped, open an online-only file | **Fails in 17 ms** with `0x80070194` "The cloud file provider exited unexpectedly" (not 0x8007016A, which applies when no provider ever connected); directory listing still works. No hang. |
| `--cleanup` (`StorageProviderSyncRootManager.Unregister`) | 581 ms. Of 171 entries, the 7 hydrated files stayed as ordinary files and the **164 unhydrated placeholders were deleted**; SyncRootManager key removed. Confirms the data-loss semantics of unregistering. |
| `NormalizedPath` in FETCH_DATA | Empty in this callback (populated in the RENAME/DELETE callbacks). Do not rely on it for FETCH_DATA; use `FileIdentity` (we store the Graph item id there) and `FileId`. |

Implications (to evaluate next):
1. The user-visible risk for "never hang" is not our callbacks (microseconds) but **open-time blocking for large files**: opening a 4 GB video blocks the opening app for the entire download (about 5 minutes at 13 MB/s). Explorer itself did not hang in our tests because we never opened files through the shell, so this needs a manual Explorer/media-player test (runbook F.4 f).
2. Hypotheses for why PROGRESSIVE behaves like FULL: (a) the first FETCH_DATA is always issued for the whole file at open and the platform only completes the user IO once the provider has transferred the range the IO needs, and our provider transfers strictly sequentially from 0; (b) an app-side policy of FULL wins (`max(app, provider)`), e.g. triggered by antivirus or by the open's access mask; (c) a behaviour change in this Insider-class build (26H2, 26300.x). Next experiments: log the optional range and further FETCH_DATA callbacks for the same file while a reader holds it open; try transferring the *middle* range first and see whether the blocked ReadFile completes before the rest arrives; open with `FILE_READ_ATTRIBUTES` only (should not hydrate); test with an Explorer double-click and a media player.
3. Until resolved, design the engine so that large-file opens are cancellable and visibly progressing (tray + CfReportProviderProgress), and consider `CF_HYDRATION_POLICY_PARTIAL`.

## Observed on the developer's PC (2026-11-14, Windows 11 26H2 build 26300.9974, cldflt.sys 10.0.26100.9974)

- `StorageProviderSyncRootManager.Register` from an **unpackaged Native AOT exe** succeeded (CsWinRT projection on net11.0-windows10.0.26100.0; id `NoDriveSpike!<SID>!Personal`), while OneDrive.exe was running with its own root at `%USERPROFILE%\OneDrive`. `CfConnectSyncRoot` with `REQUIRE_PROCESS_INFO | BLOCK_SELF_IMPLICIT_HYDRATION` returned a key; `CfCreatePlaceholders` created 200 entries (folders with `DISABLE_ON_DEMAND_POPULATION`, files `MARK_IN_SYNC`) with no per-entry errors. `CfDisconnectSyncRoot` returned S_OK on quit.
- Idle connected provider: 10.6 MB private bytes, 47.5 MB working set, 11-19 threads, ~578 handles (3 min). No callback was triggered in this run (nothing opened in Explorer), so hydration, acks and Explorer rendering are still unverified.
- OneDrive's own sync-root id on this PC: `OneDrive!<SID>!Personal|<32-hex drive id>`, so match on the prefix `OneDrive!<SID>!Personal` (answers an open question in `onedrive-client-coexistence.md`).
- `HKCU\Software\Microsoft\OneDrive\Accounts` also contains a `FileCoAuth` key with no `UserEmail`/`UserFolder` (Office co-authoring pseudo-account): detection must ignore account keys without `UserEmail`.

## 1. Platform overview

### Layers
| Layer | What it is | Notes |
|---|---|---|
| `cldflt.sys` | Kernel minifilter; implements placeholders as reparse points | NTFS only. Reparse points are hidden from every process except the sync provider and `%systemroot%` images; apps can opt in via `RtlSetProcessPlaceholderCompatibilityMode`. |
| `CldApi.dll` (`cfapi.h`) | Win32 user-mode library, the `Cf*` functions | 36 functions in Win32Metadata namespace `Windows.Win32.Storage.CloudFilters` (verified against the windows-rs docs generated from the same winmd). Vanara's readme says "34 of 34"; the two-function discrepancy is not explained in the research sources (to verify against the function list before relying on Vanara for a specific call). |
| `Windows.Storage.Provider` (WinRT) | Sync-root registration and Shell configuration (`StorageProviderSyncRootManager`, `StorageProviderSyncRootInfo`) | CloudFilesContract 10.0.16299+; `ProviderId` added 17763; `FallbackFileTypeInfo` added 19041 but marked "scenarios not supported". No Windows App SDK needed. |

Sources: https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine , https://microsoft.github.io/windows-docs-rs/doc/windows/Win32/Storage/CloudFilters/index.html , https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.storageprovidersyncrootmanager?view=winrt-26100

### What registering a sync root gives for free
- Placeholder files (~1 KB each), three user-visible states (placeholder / full / pinned full).
- Automatic hydration-state icons in the Status column (replaces icon-overlay shell extensions).
- Hydration progress in Explorer plus interactive toasts for background hydration.
- Branded navigation-pane node (icon + display name).
- Standard context-menu verbs "Always keep on this device" / "Free up space" (see pitfall 8 on where they now live).
- Optional: custom states, thumbnails, extended properties, copy hook (Insider 19624+), Share handler (Win11 21H2+), search handler (Win11 24H2, Copilot+ PCs only).

Source: https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine

### What cfapi does NOT do (by design)
Quoted from the Q&A FAQ (community-reported, vendor moderator, Nov 2025): "The Cloud Filter API is not a full sync engine. It only manages placeholders and hydration. Version tracking, conflict resolution, and metadata are your responsibility." Concretely:
- No callback for local file **creation** or **content edits**; only deletes and renames of placeholders are reported. Use `ReadDirectoryChangesW` and/or the NTFS USN journal with a persisted cursor.
- No automatic rollback of a cancelled/partial hydration: bytes already written stay on disk.
- No logging API (see Diagnostics).

Sources: https://learn.microsoft.com/en-us/answers/questions/2288103/cloud-file-api-faq , https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine

### Requirements
- Min client Windows 10 1709, **desktop apps only** (UWP cannot implement a sync engine).
- NTFS only (not ReFS/Dev Drive). Never a drive root (community-reported via Nextcloud PR #3290 / Microsoft Q&A; URL not captured in the research, to verify in spike).
- Provider needs `WRITE_DATA` or `WRITE_DAC` on the sync-root folder for `CfRegisterSyncRoot`/`CfConnectSyncRoot`/`CfUnregisterSyncRoot` (else `ERROR_CLOUD_FILE_ACCESS_DENIED`). No admin rights when the root is under the user's profile.
- No two sync roots may overlap (ancestor/descendant). Windows 11 ships OneDrive preinstalled as a sync root, so NoDrive needs its own `ProviderId`/`Id` and a non-overlapping folder (`%USERPROFILE%\NoDrive`).

Sources: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfregistersyncroot , https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfconnectsyncroot

### Hydration policies and modifiers (`CF_HYDRATION_POLICY`)
- Order: `ALWAYS_FULL > FULL > PROGRESSIVE > PARTIAL`. Effective policy at file-open = **max(app policy, provider policy)**; default `PROGRESSIVE`; cannot change after open.
- `FULL` forces a whole-file download on first byte access for every app; conflicts with the low-footprint goal for large media. `PROGRESSIVE` is the lighter choice but the provider **must handle random offsets**, not sequential-from-0.
- Modifiers: `VALIDATION_REQUIRED` (then `VALIDATE_DATA` callback is mandatory), `STREAMING_ALLOWED` (platform may not persist hydrated data; mutually exclusive with validation), `AUTO_DEHYDRATION_ALLOWED` (lets the platform call `CfDehydratePlaceholder` itself; **without it, the only supported dehydration path is the user changing pinned/unpinned attributes and the provider dehydrating asynchronously**).
- NoDrive decision: `PROGRESSIVE | AUTO_DEHYDRATION_ALLOWED`. Graph documents HTTP Range on `@microsoft.graph.downloadUrl` (206; may fall back to 200 full body; URL may expire within minutes). The FETCH_DATA worker must tolerate a 200 ("write ahead from offset 0") and refresh the downloadUrl per request. Source: https://learn.microsoft.com/en-us/graph/api/driveitem-get-content

### Population policies (`CF_POPULATION_POLICY`)
- `ALWAYS_FULL` (no `FETCH_PLACEHOLDERS` ever; the provider pre-creates every placeholder), `FULL`, `PARTIAL` (per-folder on-demand enumeration via `FETCH_PLACEHOLDERS`).
- For a 200k-500k item drive, `ALWAYS_FULL` means one `CfCreatePlaceholders` entry + MFT record per item up front (minutes of disk I/O) and a full redo after a 410 resync. **NoDrive decision: `PARTIAL`, with `FETCH_PLACEHOLDERS` answered from the local SQLite index, never from live Graph calls.**
- Useful register flags: `CF_REGISTER_FLAG_UPDATE` (re-register idempotently; Nextcloud does this at every start), `CF_REGISTER_FLAG_MARK_IN_SYNC_ON_ROOT`, `CF_REGISTER_FLAG_DISABLE_ON_DEMAND_POPULATION_ON_ROOT`.

Sources: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfregistersyncroot , critique gap on population (above), https://learn.microsoft.com/en-us/graph/api/driveitem-delta?view=graph-rest-1.0

### Other policies
- InSync policy: choose one deliberately. Report suggested data + LastWriteTime; CloudMirror uses creation time; NoDrive decision: `FileLastWriteTime | DirectoryLastWriteTime` so a local edit marks the item NotInSync.
- Hardlink policy; Placeholder-management policy (`CREATE/CONVERT/UPDATE_UNRESTRICTED`, lets a non-connected helper process create/convert placeholders) requires `CfGetPlatformInfo().IntegrationNumber >= 0x310`.

Source: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfregistersyncroot

### Integration numbers / OS builds relevant to us
- API baseline identical on Windows 10 1709+ and Windows 11. Windows 11-only: Share handler (21H2), search handler (24H2 Copilot+), and the fix for the 2004-era `AckDelete` bug (fixed per reporter on build 22000.194, never confirmed fixed on Win10). Windows 11-only regression: 24H2/25H2 `CfUpdatePlaceholder` heap corruption (pitfall 2).
- Windows 11 25H2 = build 26200, an enablement package over 24H2's 26100, so both share `cldflt.sys`/`CldApi.dll` servicing. Source: https://blogs.windows.com/windows-insider/2025/08/29/releasing-windows-11-version-25h2-to-the-release-preview-channel/
- Status-UI flyout interface (`IStorageProviderStatusUISourceFactory`) is "introduced in 10.0.23504.0" (a 2023 Insider build later than 22H2 RTM 22621): plan for 23H2 (22631)+ and feature-detect with `ApiInformation.IsTypePresent`. Source: https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.istorageproviderstatusuisourcefactory?view=winrt-26100
- Windows 10 scope note: Win10 Home/Pro end of support 2025-10-14, but consumer ESU reportedly runs to 2027-10-12 (Microsoft pages inconsistent) and commercial ESU up to three years. Windows 11-only is a legitimate *effort* decision, not a "Win10 is dead" fact. Sources: https://learn.microsoft.com/en-us/lifecycle/products/windows-10-home-and-pro , https://learn.microsoft.com/en-us/windows/whats-new/extended-security-updates , https://pureinfotech.com/windows-10-esu-extended-2027-microsoft-update/
- NoDrive floor: Windows 11 24H2 with the Aug 2025 cumulative update (KB5063878, `cldflt.sys` 10.0.26100.4946) or later, because of CVE-2025-50170 (pitfall 13).

---

## 2. Process model and lifetime

- **Ordinary per-user desktop process in the interactive session** (tray/background app started at logon). Not a Windows service: hydration triggered from session 0 fails with `STATUS_CLOUD_FILE_ACCESS_DENIED` (0xC000CF18 / Win32 395) unless the accessing image is under `%systemroot%`. A Microsoft Q&A responder (Junjie Zhu, described as Microsoft's by the fact-checker) reproduced it in June/July 2022 and asked for GitHub issue #245, which is still open with **zero Microsoft responses** (last comment 2023-11-27); treat as community-reported. Not elevated either (UIPI/drag-drop issues, not needed). Sources: https://learn.microsoft.com/en-us/answers/questions/892349/cfapi-allow-data-fetch-for-services , https://github.com/microsoft/Windows-classic-samples/issues/245
- **Exactly one `CfConnectSyncRoot` connection per sync root at a time.** Connection does not survive process exit; registration does (persistent across reboots). Source: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfconnectsyncroot
- **The provider must stay running.** When it is not: hydrated files and already-populated directories keep working, but opening an unhydrated placeholder fails in ~50-70 ms with `ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING` (Win32 362 / HRESULT 0x8007016A, the familiar OneDrive error), and unpopulated directories cannot be enumerated. This is why idle memory must come from the provider's own design (Native AOT, no UI framework, disk-backed index, buffers returned after each transfer), not from exiting. Sources: https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine , https://github.com/B3nedikt/Tenshu/pull/6 , https://windows-hexerror.linestarve.com/0x8007016A , https://help.nextcloud.com/t/unknown-disk-usage-and-the-cloud-file-provider-is-not-running/251240.md
- Hydrations interrupted by a crash/power loss arrive on the next request with `CF_CALLBACK_FETCH_DATA_FLAG_RECOVER`. Handle it (resume from the first un-transferred offset).
- A crashed provider leaves its registration behind. For the CloudMirror sample that caused Explorer to relaunch on every click (its registration references a package that prompts on activation); for an unpackaged exe the effect is 0x8007016A everywhere until restart. Hence: HKCU Run autostart, single-instance mutex, Task Scheduler restart-on-failure, graceful `CfDisconnectSyncRoot` on `WM_QUERYENDSESSION`, re-add tray icon on `TaskbarCreated`, **never unregister on normal exit**.
- Shutdown order: `CfDisconnectSyncRoot` first; `CfUnregisterSyncRoot` fails with `ERROR_CLOUD_FILE_INVALID_REQUEST` while connected. Source: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfunregistersyncroot
- Fast User Switching / multiple interactive users: per-user roots (the Id embeds the SID), one NoDrive per session. Untested: open question for the spike.
- Callbacks arrive on an **arbitrary threadpool thread owned by CldApi.dll inside our process**; several run concurrently; the provider does its own synchronization. Idle cost of that threadpool is unmeasured (open question).

---

## 3. Registration

### Option A: WinRT `StorageProviderSyncRootManager.Register` (NoDrive default)
- `StorageProviderSyncRootInfo.Id` format `[ProviderID]![Windows SID]![AccountID]`, max 174 chars. `Path` must be a `StorageFolder` (`StorageFolder.GetFolderFromPathAsync`). `IconResource` is a `path,index` string. Set `HydrationPolicy`, `HydrationPolicyModifier`, `PopulationPolicy`, `InSyncPolicy`, `ShowSiblingsAsGroup=false`, `DisplayNameResource`, `ProviderId`.
- Writes the nav-pane and `SyncRootManager` registry glue for you; the Register docs state **no identity or elevation requirement**. Community: the Q&A FAQ moderator recommends Register over `CfRegisterSyncRoot` ("use one, not both").
- **Fail-fast hazard:** calling Register on a path already registered by another provider (or nested in another root) raises an **uncatchable fail-fast** (Infomaniak kDrive PR #2532, merged 2026-10-07, linked issue #2596). Always probe with `CfGetSyncRootInfoByPath` before Register. Sources: https://github.com/Infomaniak/desktop-kDrive/pull/2532 , https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfregistersyncroot
- Register threw 0x8000000B in the packaged CloudMirror case when the manifest `Extensions` element was missing (https://github.com/microsoft/Windows-classic-samples/issues/119); not applicable unpackaged but a hint that Register inspects app context.
- A packaged app whose manifest lacked the `windows.cloudFiles` extension could still Register but left stale `SyncRootManager`/`NameSpace` keys behind after Unregister (community-reported: https://learn.microsoft.com/en-us/answers/questions/5551232/storageprovidersyncrootmanager-unregister-sync-roo). Do not mix packaged and unpackaged dev builds against the same Id.
- `StorageProviderSyncRootManager.Unregister` docs: "Desktop apps that are not packaged using MSIX must call this method when uninstalled" (packaged apps are auto-unregistered). This is the clearest first-party acknowledgment that unpackaged providers are a supported configuration, and it means NoDrive's uninstaller must call it. Source: https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.storageprovidersyncrootmanager.unregister?view=winrt-26100

Sources: https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.storageprovidersyncrootmanager.register?view=winrt-26100 , https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.storageprovidersyncrootinfo.id?view=winrt-26100 , https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.storageprovidersyncrootinfo?view=winrt-26100

### Option B: Win32 `CfRegisterSyncRoot` + registry (fallback if the CsWinRT/.NET 11 AOT path fails)
- `CfRegisterSyncRoot(path, CF_SYNC_REGISTRATION, CF_SYNC_POLICIES, CF_REGISTER_FLAG_UPDATE)` registers the root with the filter only. Nav-pane node, display name, icon and handler CLSIDs must be written by hand:
  - `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\<provider!SID!account>`: `Flags` (Nextcloud uses 34, or 2 to hide the cfapi verbs; other bits undocumented), `DisplayNameResource`, `IconResource` (`<exe>,0`), `UserSyncRoots\<SID>`, optional `CustomStateHandler`, `ThumbnailProvider`, `NamespaceCLSID`.
  - HKCU CLSID namespace extension (`IsPinnedToNameSpaceTree=1`, `SortOrderIndex`, `InProcServer32=shell32.dll`, `Instance` CLSID `{0E5AAE11-...}`, `InitPropertyBag` with `TargetFolderPath`, `ShellFolder` attributes, `Desktop\NameSpace`, `HideDesktopIcons`).
- **Risk:** the `SyncRootManager` key is under HKLM. Nextcloud treats a failed HKLM write as non-fatal and silently continues without shell integration. Whether the key ACL allows standard-user writes could not be verified from primary sources, so an unelevated NoDrive using Option B could silently lose nav-pane/Status branding. Prefer Option A; keep B as a documented fallback and test both in the spike.

Sources: https://learn.microsoft.com/en-us/windows/win32/shell/integrate-cloud-storage , https://raw.githubusercontent.com/nextcloud/desktop/master/src/libsync/vfs/cfapi/cfapiwrapper.cpp , https://raw.githubusercontent.com/nextcloud/desktop/master/src/gui/navigationpanehelper.cpp (GPL, read for registry layout only)

### Package identity: what needs it and what does not
| Feature | Needs package identity? | Evidence quality |
|---|---|---|
| Sync root registration, placeholders, hydration, progress, toasts | **No** | Medium-high: Register docs state no requirement; Unregister docs explicitly address unpackaged apps; Tenshu PR #6 (2026-11-10, JVM FFM spike) measured "no package identity is needed when running unpackaged", registration/placeholders/hydration ALL PASS on Windows 11; gkrost/unidrive-windows issue #38 reported the same (repo now 404); Nextcloud/ownCloud ship unpackaged. |
| Nav-pane node, Status column icons, built-in verbs | **No** | Medium: Tenshu checked the nav-pane entry only "by eye"; Nextcloud does it via registry. **To verify in spike on the target build** (plan kill criterion). |
| CopyHook, ShareHandler, SearchHandlerFactory | No | High: documented as registry values under the sync root key pointing to a COM local-server CLSID. |
| Status-UI flyout (`IStorageProviderStatusUISourceFactory`) | Probably not | Medium: docs say register "in the app manifest **or registry**"; `IStorageProviderKnownFolderSyncInfoSourceFactory` docs give an explicit unpackaged path under `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StorageProvider`, "consistent with the existing mechanism for unpackaged provider registration". Exact value name for the status-UI factory undocumented; try registry before adopting a sparse package. |
| CustomStateHandler, ThumbnailProviderHandler | Probably not | Medium: Nextcloud writes `CustomStateHandler`/`ThumbnailProvider` CLSIDs under the HKLM `SyncRootManager` key; the appx route (`desktop3:CloudFiles`) is the documented one. |
| ExtendedPropertyHandler, BannersHandler, ContentUriSource (`desktop4`) | Unknown (no documented registry route) | Low. |
| Windows 11 modern context-menu entries (`IExplorerCommand`), manifest `StartupTask` | **Yes** (plus a native in-proc DLL for the menu) | High. |

Microsoft's conceptual page still says "Sync engines using the cloud files APIs are designed to use the Desktop Bridge as an implementation requirement" (page updated 2025-11-18); the API reference pages contradict that for the core path. If identity is ever needed: signed sparse "package with external location" (`AllowExternalContent`, Windows 10 2004+/10.0.19041), certificate trusted on the target machine (Azure Trusted Signing or similar for end users; self-signed in `CurrentUser\TrustedPeople` for self-use).

Sources: https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine , https://github.com/B3nedikt/Tenshu/pull/6 , https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/CloudMirror/CloudMirrorPackage/Package.appxmanifest , https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.istorageproviderstatusuisourcefactory?view=winrt-26100 , https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.istorageproviderknownfoldersyncinfosourcefactory?view=winrt-26100 , https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/grant-identity-to-nonpackaged-apps , https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop3-cloudfiles , https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/integrate-packaged-app-with-file-explorer

### Sync root location
- `%USERPROFILE%\NoDrive`: implicit `WRITE_DATA`, outside OneDrive's root, not a drive root, NTFS.
- Add it to the Windows Search crawl scope (`ISearchCrawlScopeManager`, as CloudMirror does) unless the spike shows Status icons are correct without it (pitfall 5).

---

## 4. Callbacks

Registered as an array of `CF_CALLBACK_REGISTRATION` terminated by `CF_CALLBACK_REGISTRATION_END`, passed to `CfConnectSyncRoot`; "the sync provider only needs to register the callbacks it implements". Connect flags for NoDrive: `CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO` (image path of the hydrating process in `CF_PROCESS_INFO`, for telemetry and AV detection) `| CF_CONNECT_FLAG_BLOCK_SELF_IMPLICIT_HYDRATION`.

`CF_CALLBACK_INFO` provides `FileId`, `FileSize`, `FileIdentity` blob (set at placeholder creation, max 4 KB; NoDrive stores the Graph item id), `NormalizedPath`, `TransferKey`, `PriorityHint` (0-15), `RequestKey`, optional `CF_PROCESS_INFO`.

Sources: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ne-cfapi-cf_callback_type , https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfconnectsyncroot , https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ns-cfapi-cf_callback_info

### Callback table (13 types)
| `CF_CALLBACK_TYPE_*` | Mandatory? | Blocks the user? | NoDrive handling |
|---|---|---|---|
| `FETCH_DATA` | Yes, unless hydration policy `ALWAYS_FULL` | Yes: the app's read blocks until data is transferred or the request fails | Validate, enqueue `{TransferKey, RequestKey, FileId, Offset, Length, RECOVER flag, PriorityHint}` to a bounded channel, return. Worker: refresh downloadUrl, Range GET, stream into `CfExecute TRANSFER_DATA` in aligned pooled chunks, `CfReportProviderProgress` at least every 15 s, fail on budget overrun. |
| `CANCEL_FETCH_DATA` | Optional | No | Cancel the worker's `CancellationTokenSource` keyed by `TransferKey`. Fired on Explorer "Cancel" of an implicit hydrate and on the 60 s timeout (`CF_CALLBACK_CANCEL_FLAG_IO_TIMEOUT`); **not** fired when the user cancels an "Always keep on this device" pin: keep our own cancellation for bulk pin downloads. |
| `VALIDATE_DATA` | Only with `VALIDATION_REQUIRED` modifier | Yes | Not registered (we do not use the modifier). |
| `FETCH_PLACEHOLDERS` | Yes, unless population policy `ALWAYS_FULL` | Yes: directory enumeration blocks | Enqueue; worker reads children from the SQLite index (never Graph), answers in batches via `CfExecute TRANSFER_PLACEHOLDERS` with `CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION` on the last batch. Must finish a 30k-child folder well inside 60 s (P0 measurement). |
| `CANCEL_FETCH_PLACEHOLDERS` | Optional | No | Cancel the enumeration worker. |
| `NOTIFY_FILE_OPEN_COMPLETION` | Optional | No | Not registered in v1 (telemetry only). |
| `NOTIFY_FILE_CLOSE_COMPLETION` | Optional | No | Not registered in v1; P2 may use it to trigger upload-after-close. |
| `NOTIFY_DEHYDRATE` | Optional | Yes (ack) | **Does not fire for Explorer "Free up space"** (pitfall 1). Register anyway for completeness; real path is the UNPINNED attribute watcher. |
| `NOTIFY_DEHYDRATE_COMPLETION` | Optional | No | Same caveat. |
| `NOTIFY_DELETE` | Optional, **but Explorer blocks until acked** | **Yes** | Ack **immediately** from local state (`CfExecute ACK_DELETE`, allow), journal the remote delete in SQLite for the sync engine. Idempotent on `RequestKey`/`FileId` (pitfall 9). Never wait on the network. Deny only for policy reasons known locally (e.g. mass-delete guard tripped). |
| `NOTIFY_DELETE_COMPLETION` | Optional | No | Mark journal entry as committed locally. |
| `NOTIFY_RENAME` | Optional, **but Explorer blocks until acked** | **Yes** | Ack immediately (`CfExecute ACK_RENAME`), journal the remote move/rename (moves out of the root: a hydrated item becomes a remote delete + a local plain file; a dehydrated placeholder is denied with a failing ack in v1, because hydrating inside the ack would block Explorer's dialog; see `systemPatterns.md` 3e). Never wait on the network. |
| `NOTIFY_RENAME_COMPLETION` | Optional | No | Mark committed. |

Rules that fall out of this table (non-negotiable; see plan section "Never hang Explorer"): no I/O inside any callback; every pending request has a watchdog well under 60 s; delete/rename acks are never delayed by the network; FETCH_PLACEHOLDERS never calls Graph. An unanswered delete ack is exactly what produces 0x800701AA when deleting an empty folder (community-reported in the FAQ), and slow callbacks are the mechanism behind the Explorer freezes the user saw with OneDrive.exe and that Nextcloud 33.0-35.0 users reported (30-40 s freezes in ~360k-file VFS folders; root cause undiagnosed publicly, issue nextcloud/desktop#10150 still open; do not cite as solved). Sources: https://learn.microsoft.com/en-us/answers/questions/2288103/cloud-file-api-faq , https://help.nextcloud.com/t/windows-11-explorer-freez-with-nextcloud-client-33-0-0-to-35-0-0-fixed-with-35-0-1-update/243345 , https://github.com/nextcloud/desktop/releases/tag/v35.0.1

### The 60 s deadline
- "Every callback request has a fixed 60 second timeout. A valid operation on any pending requests from the sync provider resets the timers of all pending requests." (`CF_CALLBACK_TYPE` remarks.) `CfExecute` remarks: resets timers of all pending callback requests in the same provider process. `CfReportProviderProgress` remarks: resets "the 60 second timeout period corresponding to the CF_CALLBACK_TYPE_FETCH_DATA callback". Expiry delivers `CANCEL_FETCH_DATA` with `CF_CALLBACK_CANCEL_FLAG_IO_TIMEOUT` and the app sees 0x800701AA.
- Not configurable (official docs give the number; the FAQ moderator only says "fixed internal deadline").
- **Reliability hazard (community-reported, FAQ thread comment 2026-10-02):** after a `FETCH_PLACEHOLDERS` 0x800701AA timeout, Explorer stopped sending callbacks for that folder until a full restart. One missed enumeration deadline can brick a folder for the session, hence the index-only rule and the latency histogram in P0.
- Junction with Graph (critique gap): a 429/503 `Retry-After` longer than the remaining budget, a downloadUrl expiring mid-transfer, or a 200 full body on a Range request all happen inside a pending FETCH_DATA. Worker policy: progress ping at least every 15 s even with zero new bytes; if `Retry-After` exceeds the remaining budget, fail the request (see below) so Explorer shows an error instead of hanging; refresh downloadUrl on 401/403 mid-stream; treat 200-on-Range as write-ahead from offset 0.

Sources: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ne-cfapi-cf_callback_type , https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfexecute , https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfreportproviderprogress , https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ns-cfapi-cf_callback_parameters , https://learn.microsoft.com/en-us/answers/questions/2288103/cloud-file-api-faq

### `CfReportProviderProgress`
- `CfReportProviderProgress(ConnectionKey, TransferKey, ProviderProgressTotal, ProviderProgressCompleted)`; drives Explorer's inline progress and resets the FETCH_DATA timer. A `CfReportProviderProgress2` variant exists in the function list; its extra parameters were not covered by the research pass (check the API reference before using it). Nextcloud calls `CfReportProviderProgress` per transferred block. Source: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfreportproviderprogress

### `CfExecute` operations and alignment
- `CF_OPERATION_TYPE`: `TRANSFER_DATA`, `RETRIEVE_DATA`, `ACK_DATA`, `RESTART_HYDRATION`, `TRANSFER_PLACEHOLDERS`, `ACK_DEHYDRATE`, `ACK_DELETE`, `ACK_RENAME`. Each takes `CF_OPERATION_INFO` (type, `ConnectionKey`, `TransferKey`, `CorrelationVector`, `RequestKey`) and `CF_OPERATION_PARAMETERS`.
- Hydration is inherently chunked: answer FETCH_DATA with one or more `TRANSFER_DATA` calls covering the requested `[Offset, Length)`. Alignment rules are in `CF_OPERATION_PARAMETERS`: offset and length must be sector/4096-byte aligned except for the final block reaching EOF (Nextcloud sends 4096-aligned blocks, only the last unaligned). A PROGRESSIVE provider may be asked for arbitrary ranges; it may transfer more than requested (write-ahead) but must cover the requested range.
- Cancelled hydrations leave partially written ranges on disk; no automatic rollback (the FAQ suggests `CfDehydratePlaceholder` to clean up). `CfGetPlaceholderRangeInfo` (present in the Win32Metadata function list; its semantics were not covered by the research pass, to verify in spike) should report which ranges are already on disk, useful for `RECOVER`.

Sources: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfexecute , https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ns-cfapi-cf_callback_parameters , https://learn.microsoft.com/en-us/answers/questions/2288103/cloud-file-api-faq , https://raw.githubusercontent.com/nextcloud/desktop/master/src/libsync/vfs/cfapi/cfapiwrapper.cpp

### Failing a request correctly
- **`CF_OPERATION_ACK_DATA_FLAG_CANCELED` does not exist.** The FAQ moderator's sample used it; only `CF_OPERATION_ACK_DATA_FLAG_NONE` is documented (confirmed 2025-11-25 in the thread). Do not go looking for a cancel flag.
- The real way to fail or abort a hydration: complete the request with a `CfExecute` `TRANSFER_DATA` (or `ACK_DATA`) whose `CF_OPERATION_PARAMETERS.TransferData.CompletionStatus` is a failing `NTSTATUS` (e.g. `STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE`, `STATUS_CLOUD_FILE_UNSUCCESSFUL`, `STATUS_CLOUD_FILE_ACCESS_DENIED`). The app's read then fails with a mapped error instead of waiting for 0x800701AA. Same for `TRANSFER_PLACEHOLDERS.CompletionStatus` on enumeration, and `ACK_DELETE`/`ACK_RENAME` with a failing status to deny the operation.
- A stuck request is never left pending: either data, or a failing CompletionStatus, always before the watchdog budget expires.

Sources: https://learn.microsoft.com/en-us/answers/questions/2288103/cloud-file-api-faq , https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfexecute

### Other primitives NoDrive uses
`CfCreatePlaceholders` (batch, per parent directory, with `FileIdentity`), `CfConvertToPlaceholder` (after uploading a new local file), `CfSetInSyncState`, `CfSetPinState`, `CfUpdatePlaceholder` (minimise: pitfall 2), `CfHydratePlaceholder` (for PINNED), `CfDehydratePlaceholder` (for UNPINNED, pitfall 12), `CfOpenFileWithOplock` + `CfCloseHandle` (never fight a user's open file; break our own oplock immediately when asked), `CfGetPlaceholderInfo`/`CfGetPlaceholderStateFromAttributeTag`, `CfGetSyncRootInfoByPath`, `CfGetPlatformInfo`, `CfQuerySyncProviderStatus`/`CfUpdateSyncProviderStatus`/`CfReportSyncStatus`. Function list: https://learn.microsoft.com/en-us/windows/win32/cfapi/cloud-files-functions

---

## 5. .NET interop

### Win32 side: Microsoft.Windows.CsWin32 0.3.371 (recipe)
- Latest stable 0.3.371, published 2026-11-10, depends on `Microsoft.Windows.SDK.Win32Metadata >= 71.0.45-preview`. All 36 `Cf*` functions are in the metadata. MIT.
- AOT recipe (documented): `<CsWin32RunAsBuildTask>true</CsWin32RunAsBuildTask>` (generates from an MSBuild task, emits `LibraryImport`/`GeneratedComInterface`-style code), `<DisableRuntimeMarshalling>true</DisableRuntimeMarshalling>` ("strongly recommended"), `"allowMarshaling": false` in `NativeMethods.json` (raw-pointer signatures). `AllowUnsafeBlocks` must stay true (the package defaults it; setting it false breaks generated code). Built-in COM interop generated by CsWin32 is explicitly **not** AOT-compatible; the AOT path is ComWrappers-based, which matters only for shell COM servers (P2), not for the flat `Cf*` P/Invokes.
- Callbacks: `CF_CALLBACK` is a C function pointer, so handlers are `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]` static methods placed in `CF_CALLBACK_REGISTRATION.Callback`. Natural under `DisableRuntimeMarshalling`, but **no end-to-end sample of `CfConnectSyncRoot` callbacks under Native AOT exists**; treat as plausible-but-unproven until the spike runs it.
- `NativeMethods.txt` for v1: `CfRegisterSyncRoot CfUnregisterSyncRoot CfConnectSyncRoot CfDisconnectSyncRoot CfCreatePlaceholders CfConvertToPlaceholder CfUpdatePlaceholder CfExecute CfReportProviderProgress CfSetInSyncState CfSetPinState CfHydratePlaceholder CfDehydratePlaceholder CfGetPlaceholderInfo CfGetPlaceholderRangeInfo CfOpenFileWithOplock CfCloseHandle CfGetPlatformInfo CfGetSyncRootInfoByPath` plus the Shell_NotifyIconW / window-message functions for the tray.

Sources: https://www.nuget.org/packages/Microsoft.Windows.CsWin32 , https://microsoft.github.io/CsWin32/docs/getting-started.html , https://github.com/Microsoft/CsWin32 , https://github.com/microsoft/CsWin32/pull/1497/files , https://github.com/dotnet/runtime/blob/main/docs/design/libraries/LibraryImportGenerator/Compatibility.md , https://microsoft.github.io/windows-docs-rs/doc/windows/Win32/Storage/CloudFilters/index.html

### WinRT side: CsWinRT 2.3.1 projection via TFM
- Target `net11.0-windows10.0.26100.0` (or `10.0.19041.0` with `SupportedOSPlatformVersion 10.0.17763.0`); `Microsoft.Windows.SDK.NET.Ref` (latest 10.0.28000.87, 2026-10-03, "for .NET 9 and later") is referenced automatically; no Windows App SDK. `Windows.UI.*` XAML is unsupported from desktop .NET; `Windows.Storage.Provider` is unaffected.
- CsWinRT 2.3.1 (2026-07-22) is the latest stable; AOT/trim support since 2.1.1 with Windows SDK projection build `.38`; `CsWinRTAotOptimizerEnabled` OptIn/Auto since 2.2.0; 3.0.0-preview.261104.1 (2026-11-09) targets .NET 10 and moves AOT generation into a build task. Known AOT limitation: collection expressions across the WinRT ABI; a GC race fixed in .NET 9 RC2.
- **.NET 11 status: unverified.** The desktop-WinRT doc (updated 2026-10-03) lists only net8/net9/net10 examples; CsWinRT 2.3.1 release notes say "SDK packages for .NET 9 and 10"; nothing mentions net11. **To verify in spike**: does `net11.0-windows10.0.26100.0` + `PublishAot` build and run `StorageProviderSyncRootManager.Register` + `StorageFolder.GetFolderFromPathAsync`? Fallbacks: pure `CfRegisterSyncRoot` + registry (Option B), or a tiny non-AOT helper exe that does the one-time registration.
- Pitfall NETSDK1229: a Windows SDK TFM whose revision is `1` (e.g. `10.0.26100.1`) silently references CsWinRT 3.0 preview; `10.0.28000.0` was rejected as an invalid `TargetPlatformVersion` on SDK 10.0.300. Pin `10.0.26100.0`.
- WinRT COM-server authoring (`IStorageProviderStatusUISource`, `IStorageProviderItemPropertySource`) under AOT on net11 is unproven; P2 concern.

Sources: https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-enhance , https://github.com/microsoft/CsWinRT/blob/master/docs/aot-trimming.md , https://www.nuget.org/packages/Microsoft.Windows.CsWinRT , https://github.com/microsoft/CsWinRT/releases , https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref , https://github.com/dotnet/sdk/discussions/54975 , https://devblogs.microsoft.com/dotnet/?p=61205 , https://learn.microsoft.com/en-us/dotnet/standard/native-interop/comwrappers-source-generation

### Prototyping alternative: Vanara.PInvoke.CldApi 5.0.8
- MIT, by dahall, **5.0.8 released 2026-10-24** (5.0.1 added .NET 10 support; the 4.2.0 figure in the raw report is stale). TFMs up to `net10.0-windows7.0` (NuGet will select it for a net11 project through normal TFM compatibility); depends on `Vanara.Core` and `Vanara.PInvoke.Kernel32`. Claims 100% CldApi coverage.
- Classic marshalling and SafeHandles; no AOT/trimming claim anywhere in the release notes. Use only for throwaway prototypes and for reading its `UnitTests/PInvoke/CldApi/CloudSyncProvider.cs` (696 lines: registers via `StorageProviderSyncRootManager`, connects with all 13 callbacks, raises .NET events; a good minimal skeleton, MIT).

Sources: https://www.nuget.org/packages/Vanara.PInvoke.CldApi , https://github.com/dahall/Vanara/releases , https://raw.githubusercontent.com/dahall/Vanara/master/PInvoke/CldApi/readme.md , https://github.com/dahall/Vanara/blob/master/UnitTests/PInvoke/CldApi/CloudSyncProvider.cs

---

## 6. Existing implementations

None is a drop-in production library; none has a stable release. Permissive ones are reference code to borrow idioms from; copyleft ones are protocol/registry reading only (NoDrive is MIT).

| Project | Language / licence | Maturity | Last activity | What to borrow |
|---|---|---|---|---|
| Microsoft CloudMirror (`Windows-classic-samples/Samples/CloudMirror`) | C++/WinRT, **MIT** (icons: Fluent UI System Icons, MIT) | Official sample, MSIX-packaged, "not intended to be used as production code", "an early preview, and far from final" | Sample page updated 2026-08-06; built with SDK 10.0.22598.0; VS 2019/2022/2026 | Architecture blueprint: `CloudProviderRegistrar`, `Placeholders`, `FileCopierWithProgress` (slow chunked hydration + progress), `ShellServices`, `CloudProviderSyncRootWatcher` (ReadDirectoryChangesW, UNPINNED -> dehydrate), `ISearchCrawlScopeManager` call; custom states, thumbnails, context verb, URI source, status UI. Does **not** do cloud-to-local propagation or local-edit upload. |
| styletronix/cfapiSync | C#, **MIT** | "very early alpha", 69 stars, local/UNC backends only, bidirectional, FileSystemWatcher-based | Changelog 2021; no commits since 2025-08-18 | `SyncProvider`/`IServerProvider` split, pin-state driven hydrate/dehydrate handling. |
| MirrorPulse/CfSharp | C#, **Apache-2.0** | Previews only, no stable 1.0, no NuGet, 4 stars / 0 forks; requires .NET 10 SDK (global.json); x64/ARM64; packages CfSharp.Native / CfSharp / CfSharp.Storage.Sqlite + mirror sample | Last commit 2026-09-27 (222 commits) | Modern .NET 10 shape of a thin native layer + SQLite state store; hand-written LibraryImport approach. |
| HRAshton/WinCfApiWrapper | C#, **0BSD** | Read-only content sources only; single maintainer, 0 GitHub stars, 799 NuGet downloads | NuGet 1.2.2 on 2026-11-12, `net8.0-windows7.0` | Registration/unregistration and hydration plumbing patterns. |
| Vanara `UnitTests/.../CloudSyncProvider.cs` | C#, **MIT** | Internal test harness | Vanara 5.0.8 (2026-10-24) | Minimal 13-callback skeleton (see above). |
| PrimalZed/CloudSync (+ secdev02 fork) | C#, **GPL-3.0** (the raw report wrongly implied permissive) | Vanara-based port of CloudMirror; PopulationPolicy Full; large uploads "tend to break"; no persistence; thumbnails never worked; status UI not implemented; CsWin32 only a TODO | 14 stars, 65 commits | **Inspiration only** (copyleft). |
| Knight1988/TestCloudSyncEngine | C# | Archived Nov 2025, **no licence** | - | Do not copy. |
| Nextcloud desktop `src/libsync/vfs/cfapi` (`cfapiwrapper.cpp`, `hydrationjob.cpp`, `vfs_cfapi.cpp`, `shellext/`) + `gui/navigationpanehelper.cpp` | C++/Qt, **GPL-2.0-or-later** | The most battle-tested third-party cfapi code | v34.0.7 2026-11-12; v35.0.1 2026-10-29 | Read only: registry layout (Option B), `CF_REGISTER_FLAG_UPDATE` at every start, Hydration FULL / Population PARTIAL / `PRESERVE_INSYNC_FOR_SYNC_ENGINE`, rejects FETCH_DATA from its own process (deadlock), hydration via local sockets to a sync thread, 4096-aligned `TRANSFER_DATA` blocks with progress per block. |
| ownCloud client `src/plugins/vfs/win` (`vfs_win.cpp`, `callbacks.cpp`, `hydrationdevice.cpp`, `validationdevice.cpp`) | C++, **GPL-2.0-or-later** (relicensed 2025) | Production | v7.1.1 | Read only: callback and validation-device structure. |
| Rust crates `cloud-filter`, `win_cf_api` | Rust | - | - | Thin, safe wrapper design reading. |

Sources: https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/cloudmirror-sample/ , https://github.com/microsoft/Windows-classic-samples/blob/main/LICENSE , https://raw.githubusercontent.com/microsoft/Windows-classic-samples/main/Samples/CloudMirror/CloudMirror/CloudProviderRegistrar.cpp , https://github.com/styletronix/cfapiSync , https://raw.githubusercontent.com/styletronix/cfapiSync/master/README.md , https://github.com/MirrorPulse/CfSharp , https://github.com/MirrorPulse/CfSharp/commits/main , https://github.com/HRAshton/WinCfApiWrapper , https://www.nuget.org/packages/WinCfApiWrapper , https://github.com/PrimalZed/CloudSync , https://github.com/secdev02/CloudSync , https://github.com/Knight1988/TestCloudSyncEngine , https://github.com/nextcloud/desktop/tree/master/src/libsync/vfs/cfapi , https://github.com/nextcloud/desktop/blob/master/COPYING , https://github.com/owncloud/client/tree/master/src/plugins/vfs/win , https://doc.owncloud.com/desktop/6.0/vfs.html

---

## 7. Known pitfalls and bugs

Evidence quality legend: **Official** = Microsoft Learn API/conceptual docs or Windows blog; **Moderator-reproduced** = a "Microsoft External Staff" vendor moderator reproduced it in a Q&A thread (still community-reported, not a Microsoft engineering position); **Community** = user reports / third-party code, no Microsoft position.

### 1. `NOTIFY_DEHYDRATE` / `_COMPLETION` do not fire on Explorer "Free up space"
- Status: open since June 2024, no fix. Evidence: Moderator-reproduced by Microsoft External Staff with CloudMirror ("is a cfapi issue", pointed to Feedback Hub). Community-reported.
- Mitigation: CloudMirror's approach. Watch `FILE_ATTRIBUTE_UNPINNED` via `ReadDirectoryChangesW` (attribute changes) and call `CfDehydratePlaceholder` yourself with an exclusive `CfOpenFileWithOplock` handle; likewise `FILE_ATTRIBUTE_PINNED` -> `CfHydratePlaceholder`. Never dehydrate a NotInSync file. Consistent with the `AUTO_DEHYDRATION_ALLOWED` docs text that without the modifier "the only supported way to dehydrate" is the attribute path.
- Sources: https://learn.microsoft.com/en-gb/answers/questions/1694157/when-will-cf-callback-type-notify-dehydrate-be-inv , https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfregistersyncroot

### 2. `CfUpdatePlaceholder` heap corruption (0xC0000374) on Windows 11 24H2 / 25H2
- Status: **open**, reported 2024-11-18 (repro repo iterate-ch/CloudFiles-HeapCorrupt, 300-update loop), reproduced on 24H2 by Microsoft External Staff 2024-11-19 (suggested a support incident, "may be no-charge if confirmed as a bug"), re-reported 2025-02-27, and 2025-11-18 on 25H2 **even with an exclusive `CfOpenFileWithOplock` handle** (worked on 22H2/23H2). Affects MSIX C++ and C# P/Invoke apps alike. No acknowledgment as a bug, no fix in the thread. Evidence: Moderator-reproduced; community-reported.
- Why it matters: `CfUpdatePlaceholder` is the call used to apply every remote metadata change. The docs only require an exclusive handle when dehydrating at the same time, so the handle workaround is not documented as a fix.
- Mitigation: minimise `CfUpdatePlaceholder`; prefer `CfSetInSyncState` / `CfSetPinState` where sufficient; avoid `CF_UPDATE_FLAG_ALWAYS_FULL` for metadata-only changes (returns `ERROR_CLOUD_FILE_DEHYDRATION_DISALLOWED` otherwise); consider delete+recreate placeholder for dehydrated items whose metadata changed. **P0 step 4: run the repro on the dev build** and decide.
- Sources: https://learn.microsoft.com/en-us/answers/questions/2120335/heap-corruption-after-using-cfupdateplaceholder , https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfupdateplaceholder , https://github.com/iterate-ch/CloudFiles-HeapCorrupt

### 3. `CreateFileMapping` / `MapViewOfFile` forces full hydration under PROGRESSIVE
- Status: reported 2024-10-13 (128 MB repro on 22621.2506); moderator only asked for repro and referred to a support incident; no determination. Evidence: Community.
- Mitigation: accept it (Photos, Office and many apps memory-map); design hydration for whole-file throughput anyway (bounded concurrency 3-4, streaming into `CfExecute`, no whole-file buffering). PROGRESSIVE still helps for sequential readers (media players, hashing tools, copy).
- Source: https://learn.microsoft.com/en-us/answers/questions/2103011/(bug-report)-cfapi-createfilemapping-causes-full-p

### 4. Antivirus / scanners trigger implicit hydration
- Status: by design. Evidence: Official (the `CF_CONNECT_FLAG_BLOCK_SELF_IMPLICIT_HYDRATION` docs exist because "Anti-Virus software scans sync provider's file system activities on non-hydrated cloud file placeholders") + vendor documentation (Resilio: Bitdefender and Symantec on-access/scheduled scans fully hydrate placeholders).
- Mitigation: connect with `BLOCK_SELF_IMPLICIT_HYDRATION` (our own file I/O never hydrates); `REQUIRE_PROCESS_INFO` to log the hydrating image and optionally refuse known scanner images with a failing CompletionStatus; bounded hydration semaphore; pause on metered networks; test with Defender on. Keep the provider's own I/O off the sync root.
- Sources: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfconnectsyncroot , https://www.resilio.com/documentation/content/advanced-configuration/agents/excluding_agent_from_your_anti-malware_on-access_scanner/

### 5. Sync root should be in the Windows Search indexer scope
- Status: practical requirement. Evidence: **only** a code comment in CloudMirror quoted by a community member in a 2020 Q&A ("property queries on items will not return the expected values" if not indexed); not in the sync-engine page or API reference. Community.
- Whether `%USERPROFILE%\NoDrive` is indexed by default on Windows 11 "Classic" mode is unclear (current support page: Documents, Pictures, Music, Desktop; older docs: whole Users folder minus AppData).
- Mitigation: add the root to the crawl scope via `ISearchCrawlScopeManager` (as CloudMirror does); **to verify in spike** by comparing Status icons/progress inside vs outside an indexed folder. Do not put the root under Documents (may itself be OneDrive-redirected).
- Sources: https://learn.microsoft.com/en-us/answers/questions/96456/why-sync-root-folder-should-be-indexed-(cfapi) , https://support.microsoft.com/en-us/windows/search-indexing-in-windows-da061c83-af6b-095c-0f7a-4dfecda4d15a

### 6. `CfUnregisterSyncRoot` permanently deletes unhydrated placeholders
- Status: by design. Evidence: Official, verbatim: "If a placeholder file is not hydrated, it is permanently deleted from the local machine" and "If a placeholder directory is not fully populated, the directory is permanently deleted"; hydrated/populated items revert to normal; unrevertible items are skipped. Fails `STATUS_CLOUD_FILE_NOT_UNDER_SYNC_ROOT` if never registered and `ERROR_CLOUD_FILE_INVALID_REQUEST` while connected.
- Mitigation: never unregister on normal exit; uninstall flow = disconnect -> warn (or offer hydrate-all) -> `Unregister` -> remove folder; keep the root a dedicated folder with no user data outside the provider's control. Since the data is in OneDrive this deletes only local copies, but the user must be told.
- Sources: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfunregistersyncroot , https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.storageprovidersyncrootmanager.unregister?view=winrt-26100

### 7. Folder-level "Free up space" stays disabled unless a descendant is PINNED
- Status: reported 2026-05-12/15 (Win10 22H2, Win11 23H2, 24H2); the accepted answer (vendor moderator) said it "might be a design choice" to keep Explorer fast; accepted by the asker, not closed by Microsoft. Evidence: Community.
- Mitigation: tray-menu fallback "Free up space in folder..." that walks the index and calls `CfDehydratePlaceholder` on hydrated, InSync children.
- Source: https://learn.microsoft.com/en-us/answers/questions/5887784/folder-level-free-up-space-remains-disabled-after

### 8. Explorer hydration verbs moving into a per-provider flyout; user-customisable context menu
- Status: Insider Build 26220.7271 (KB5070307, Dev+Beta, 2025-11-21): "We've moved cloud provider options, like Always Keep on this Device and Free Up Space" into the cloud provider flyout, alongside a new "Manage file" flyout. KB5127936 (2026-10-28, optional preview, 25H2/26H2 builds 26200.9818/26300.9818) shipped a user-customisable context menu (commands/extensions can be hidden or reordered). No Microsoft statement found that the flyout change reached GA. Also: Build 26220.6760 (2025-09-29) added "StorageProvider APIs for cloud providers to integrate with File Explorer Home"; Build 26220.7070 (2025-11-07) temporarily turned that integration off. Evidence: Official (Windows Insider blog) for the Insider notes; news site for KB5127936.
- Mitigation: verify verb placement on the target build in the spike; tray-menu fallback actions ("Keep folder offline", "Free up space in folder") calling `CfSetPinState`/`CfDehydratePlaceholder`; consider whether the flyout depends on registering the StatusUI factory (unknown). File Explorer Home integration is a possible P2+ surface but unstable.
- Sources: https://blogs.windows.com/windows-insider/2025/11/21/announcing-windows-11-insider-preview-build-26100-7271-dev-beta-channels/ , https://blogs.windows.com/windows-insider/2025/11/07/announcing-windows-11-insider-preview-build-26220-7070-dev-beta-channels/ , https://blogs.windows.com/windows-insider/2025/09/29/announcing-windows-11-insider-preview-build-26220.6760-dev-channel/ , https://www.bleepingcomputer.com/news/microsoft/windows-11-kb5127936-update-released-with-41-changes-and-fixes/

### 9. Double / multiple `NOTIFY_DELETE` callbacks
- Status: by design. One user delete can produce several callbacks: move to Recycle Bin then delete, per-child callbacks on folder delete, delete-on-close, and temp-file replace-on-save sequences. Evidence: Community (FAQ moderator).
- Mitigation: idempotent handlers keyed on `RequestKey`/`FileId`; journal in SQLite before acting; remote deletes go to OneDrive recycle bin only; mass-delete threshold pauses sync and asks.
- Source: https://learn.microsoft.com/en-us/answers/questions/2288103/cloud-file-api-faq

### 10. New local files are not auto-converted; replace-on-save strips the reparse point
- Status: by design. A new file inside the root is a plain NTFS file until the provider uploads it and calls `CfConvertToPlaceholder` + `CfSetInSyncState`. Apps that save via temp-file-rename destroy the reparse point and the `FileIdentity` blob; the provider must treat the result as a new local edit and re-convert. Evidence: Community (FAQ moderator) consistent with Official docs on `CfConvertToPlaceholder`.
- Mitigation: `ReadDirectoryChangesW` watcher with 2-5 s debounce and hash/eTag compare before upload; USN replay at startup for offline changes; v1 only uploads *new* files (editing is P2).
- Sources: https://learn.microsoft.com/en-us/answers/questions/2288103/cloud-file-api-faq , https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine

### 11. Explorer holds handles on ancestor folders
- Status: Win10 19045 reproducible 100%, Win11 intermittent; blocks rename/delete of an ancestor while a 2+ level deep subfolder is open; OneDrive shows the same. Moderator redirected to the OneDrive developer forum; no technical answer. Evidence: Community.
- Mitigation: nothing provider-side; do not misreport it as a NoDrive bug; use `CfOpenFileWithOplock` for our own handles so we never add to the problem.
- Source: https://learn.microsoft.com/en-gb/answers/questions/1662213/cloud-files-api-explorer-exe-locking-folders-and-p

### 12. `CfDehydratePlaceholder` documentation is archived
- Status: the current cfapi reference has no page for it (https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfdehydrateplaceholder returns 404); the `CfRegisterSyncRoot` page links to an archived "previous-versions" page (mt827480, 2017-10-10, `is_archived: true`); a Q&A poster calls it a "preview API". The function is still exported from `CldApi.dll` and present in Win32Metadata. The archived page requires an exclusive handle (`CfOpenFileWithOplock`) "or data corruption can occur". Evidence: Official (archived).
- Mitigation: record as a risk; always use an exclusive oplock handle; keep `AUTO_DEHYDRATION_ALLOWED` so the platform can dehydrate on its own too.
- Sources: https://learn.microsoft.com/en-us/previous-versions/mt827480(v=vs.85) , https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfregistersyncroot

### 13. CVE-2025-50170 and 2025 `cldflt.sys` servicing
- Status: CVE-2025-50170 (cldflt.sys EoP, published 2025-08-12, patched in KB5063878 / `cldflt.sys` 10.0.26100.4946) has its root cause in the placeholder-creation path (`HsmpOpCreatePlaceholders` reached via `CfCreatePlaceholders`), exactly the API NoDrive calls in bulk. CVE-2025-55680 followed in KB5066835. Evidence: third-party advisory (STAR Labs) + Microsoft KB numbers.
- Mitigation: pin the minimum supported build to 24H2 with the Aug 2025 cumulative update or later; test on fully patched 24H2 and 25H2.
- Source: https://starlabs.sg/advisories/25/25-50170/

### 14. Historical: `AckDelete` broken on Windows 10 2004
- Fixed per reporter on Windows 11 22000.194; never confirmed fixed on Win10. Reason to keep Win11 as the floor. Source: https://learn.microsoft.com/en-us/answers/questions/75240/bug-report-cfapi-ackdelete-borken-on-win10-2004.html

### 15. Self-deadlock when the provider touches its own placeholders
- Nextcloud/ownCloud reject FETCH_DATA originating from their own process and hydrate via a separate thread/socket. With `BLOCK_SELF_IMPLICIT_HYDRATION` our own reads never trigger FETCH_DATA; still, never do synchronous file I/O on sync-root placeholders from a callback thread. To verify in spike. Source: https://raw.githubusercontent.com/nextcloud/desktop/master/src/libsync/vfs/cfapi/cfapiwrapper.cpp (GPL, read only)

### 16. Stale registrations and mixed builds
- Register with `CF_REGISTER_FLAG_UPDATE` semantics every start (re-register idempotently); never mix packaged and unpackaged dev builds on the same Id; a recovery path at startup should detect a stale registration (`CfGetSyncRootInfoByPath`) and reconnect. Sources: https://learn.microsoft.com/en-us/answers/questions/5551232/storageprovidersyncrootmanager-unregister-sync-roo , https://raw.githubusercontent.com/nextcloud/desktop/master/src/libsync/vfs/cfapi/cfapiwrapper.cpp

---

## 8. Diagnostics

- No logging API in cfapi (community-reported, FAQ). Built-in channels: Event Viewer `Microsoft-Windows-CloudFiles/Operational` (enable during development), ETW for `cldflt.sys`.
- Our own: structured logs per callback (`RequestKey`, `TransferKey`, `FileId`, process image from `CF_PROCESS_INFO`, latency), an in-process callback-latency histogram (acceptance: p99 < 5 ms, max < 50 ms), pending-request age gauge (acceptance: zero pending > 60 s in the 24 h soak), queue depth, private bytes.
- Error codes to recognise: `ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING` 362 / 0x8007016A (provider down); 0x800701AA `ERROR_CLOUD_FILE_REQUEST_TIMEOUT` (callback not answered in 60 s); `ERROR_CLOUD_FILE_ACCESS_DENIED` 395 / `STATUS_CLOUD_FILE_ACCESS_DENIED` 0xC000CF18 (session 0 or ACL); `ERROR_CLOUD_FILE_INVALID_REQUEST` (unregister while connected); `ERROR_CLOUD_FILE_DEHYDRATION_DISALLOWED` (`ALWAYS_FULL` update flag on metadata-only change); `STATUS_CLOUD_FILE_NOT_UNDER_SYNC_ROOT`; 0xC0000374 heap corruption (pitfall 2); 0x8000000B from Register (manifest/context problem).
- Repro suites to keep: iterate-ch/CloudFiles-HeapCorrupt; our fake Graph server with fault injection (429 with long `Retry-After`, timeouts, expired downloadUrl, network loss); CloudMirror sample for A/B comparison on the same machine.
- `CfGetPlatformInfo` at startup: log `BuildNumber`, `RevisionNumber`, `IntegrationNumber` to correlate behaviour with cumulative updates.

Sources: https://learn.microsoft.com/en-us/answers/questions/2288103/cloud-file-api-faq , https://learn.microsoft.com/en-us/answers/questions/892349/cfapi-allow-data-fetch-for-services , https://windows-hexerror.linestarve.com/0x8007016A

---

## 9. Open questions for the spike (P0)

1. Does `StorageProviderSyncRootManager.Register` from an unpackaged Native AOT process on the user's exact Windows 11 build produce the nav-pane node, icon, Status column icons and built-in verbs? Where are the verbs (top level vs provider flyout)? Kill criterion if it fails and Option B also fails.
2. Does the `net11.0-windows10.0.26100.0` CsWinRT projection build and publish under `PublishAot` with zero IL2xxx/IL3xxx warnings from our code, and what does it add to binary size and idle private bytes versus a `CfRegisterSyncRoot`-only build? Fallbacks: Option B, or a non-AOT registration helper, or .NET 10 LTS.
3. Do CsWin32 0.3.371 function-pointer callbacks (`[UnmanagedCallersOnly]` in `CF_CALLBACK_REGISTRATION`) work under Native AOT end to end (connect, FETCH_DATA, TRANSFER_DATA, acks)? No public sample exists.
4. Is the `CfUpdatePlaceholder` heap corruption present on the dev build after 2026 cumulative updates (run iterate-ch/CloudFiles-HeapCorrupt)? Decide the update strategy (minimise vs avoid).
5. Does Graph `@microsoft.graph.downloadUrl` honour Range reliably enough for PROGRESSIVE on this account (206 vs 200 frequency)? Measure.
6. Is the Status column correct with the root at `%USERPROFILE%\NoDrive` without adding it to the crawl scope? If not, confirm `ISearchCrawlScopeManager` fixes it.
7. Can the status-UI factory (and CustomStateHandler/Thumbnail CLSIDs) be registered through the registry without identity on this build, and what is the exact value name? (P2 relevance, cheap to probe.)
8. Idle private bytes / working set / thread count of a minimal AOT provider with one live `CfConnectSyncRoot` connection (CldApi threadpool inside the process), at 2 and 10 min idle and after a 1 GB hydration. No published numbers exist.
9. FETCH_PLACEHOLDERS latency from the SQLite index for a 30k-child folder, and callback-to-return latency histogram over a synthetic 500k-item drive. Confirm the "Explorer stops calling back after a FETCH_PLACEHOLDERS timeout" hazard does not bite.
10. Behaviour with Fast User Switching / two interactive sessions each running NoDrive with per-user roots.
11. Exact ACL of `HKLM\...\Explorer\SyncRootManager` for standard users (only matters for Option B).
12. Does the provider-flyout change (Insider 26220.7271) or the File Explorer Home StorageProvider integration exist on the target GA build, and does the flyout depend on a registered StatusUI source?
