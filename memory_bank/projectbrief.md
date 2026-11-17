# NoDrive: project brief

Foundation document and source of truth for scope. If another memory-bank file disagrees with this one on *what* NoDrive is or is not, this file wins; architecture details live in `systemPatterns.md`, the phased plan in `projectPlan.md`.

Created 2026-11-13 from the approved plan and the verified research pass (`research/`). Update it only when scope or a confirmed decision changes, and date the change.

## Vision

NoDrive is a from-scratch, very lightweight OneDrive (personal account) client for Windows 11, written in C# on .NET 11 with Native AOT. It replaces OneDrive.exe for one account and integrates with File Explorer through the Windows Cloud Files API (cfapi, "Files On-Demand" placeholders), so files look and behave exactly like OneDrive's online-only files in Explorer and in every application's Open/Save dialog, while the resident process is a headless engine that sits idle until a file is opened or a remote change arrives. It exists because OneDrive.exe on an 8 GB machine with hundreds of thousands of items consumed almost 4 GB of RAM, froze Explorer and left move dialogs dead; NoDrive treats responsiveness, bounded memory and correctness as hard requirements that outrank features.

## Goals

Measurable where possible. Numbers are acceptance criteria, measured on the developer's Windows 11 x64 PC (see `projectPlan.md`).

- **Never hang Explorer.** Every cfapi callback returns in microseconds; work is done asynchronously. Callback-to-return latency p99 under 5 ms, max under 50 ms, over a synthetic 500k-item drive. cfapi callbacks carry a fixed 60 s deadline (documented in the `CF_CALLBACK_TYPE` remarks: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ne-cfapi-cf_callback_type; that a missed deadline surfaces as 0x800701AA is community-reported, Microsoft Q&A FAQ: https://learn.microsoft.com/en-us/answers/questions/2288103/cloud-file-api-faq); NoDrive budgets well under it and never leaves a request pending.
- **Never leave a dialog stuck.** With the network pulled or Graph returning 429, Explorer open/copy/move/rename/delete of placeholders completes or fails with a visible error within 10 s. Delete/rename acks never wait on the network.
- **Bounded memory, independent of item count.** Idle Private Bytes under 30 MB with a 500k-item index on disk. After a 10 GB hydration burst, commit returns to within 10 MB of idle inside 60 s. A `HeapHardLimit` makes a leak fail loudly instead of eating the machine (GC knobs: https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector).
- **Scale: 500k items.** Lazy placeholder population, streaming delta ingestion into a disk-backed index, folder opens with tens of thousands of children answered from the index alone. The developer's drive measured 136,411 items (17,286 folders, 119,125 files, 460 GB) on 2026-11-13 and was 400k+ before trimming.
- **Correctness, no data loss.** Idempotent, crash-safe, journaled remote mutations; If-Match on every write; conflict copies instead of overwrites; never dehydrate a file that is not in sync.
- **Lightweight by construction.** One Native AOT process with no UI framework resident; explorer.exe renders everything. Idle footprint must be measured in P0 because no published number exists for an AOT cfapi provider (to verify in spike).
- **Transparent UX.** Nav-pane node, Status column icons, built-in "Always keep on this device" / "Free up space" verbs and hydration progress come from cfapi for free (https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine).

## Scope of v1

- **Browse**: the whole personal OneDrive appears under `%USERPROFILE%\NoDrive` as online-only placeholders. Folders are populated from the local SQLite index (answered in the callback path from the index only), heavy folders are pre-populated in the background by cost, and light folders on demand (ADR-0007).
- **Download**: opening a placeholder downloads the whole file first (hydration policy Full, ADR-0010), with Windows' own progress dialog; keep on device / free up space verbs work. A shared download rate limit is configurable. Graph's pre-authenticated `downloadUrl` is used with Range where useful and tolerates 200 responses.
- **Upload new files**: files and folders dropped or created inside the sync root are uploaded (simple PUT for small files, upload sessions with 320 KiB-aligned 5-10 MiB chunks above that: https://learn.microsoft.com/en-us/graph/api/driveitem-createuploadsession?view=graph-rest-1.0), then converted to in-sync placeholders.
- **Rename and delete propagation** of local operations with guards (acknowledged locally first, replayed to the server asynchronously, remote deletes go to the OneDrive recycle bin, mass-delete threshold pauses sync).
- **Remote change tracking**: `/me/drive/root/delta` polling (adaptive, 1 min active to 15-60 min idle, daily safety full delta) applied to the index and to existing placeholders (https://learn.microsoft.com/en-us/graph/api/driveitem-delta?view=graph-rest-1.0).
- **Explorer integration**: unpackaged sync-root registration, nav-pane node, Status column, built-in hydration verbs, hydration progress and toasts raised by cfapi.
- **Tray icon** (Shell_NotifyIcon, no UI framework): open the settings page (double-click), pause/resume, start with Windows, sign in, allow held deletions, open folder, quit.
- **Sign-in** via system browser (authorization code + PKCE, loopback redirect), refresh token protected with DPAPI. Scopes `Files.ReadWrite` + `offline_access`.
- **Lifecycle**: autostart at logon (HKCU Run, set by the MSI or the tray), per-user single instance, in-process crash restart (max 3 in 10 minutes), `--quit/--pending/--uninstall`, a per-user WiX MSI whose uninstall refuses while work is queued and unregisters the sync root (downloaded files stay).
- **Settings and activity**: a local web page served by the engine (ADR-0011) with live transfers, recent activity, bandwidth limits, sync intervals, language (English/Spanish, Windows regional formats) and guides; `config.json` for the rest. Optional "Switch from OneDrive" assistant (ADR-0013).

## Explicit non-goals for v1

- **Editing existing files in place** (save-over, Office temp-file storms, USN replay of offline edits, conflict copies for concurrent edits). Lands in P2.
- **Other storage providers.** OneDrive personal only. This is deliberate: rclone's many backends are the bloat NoDrive avoids.
- **Business / SharePoint accounts.** Not tested; the app registration keeps the option open (`AzureADandPersonalMicrosoftAccount`) but nothing else does.
- **macOS / Linux.** cfapi is Windows-only; File Provider / FUSE are at most a P3 exploration.
- **Package identity (MSIX or sparse package).** Not needed for the core cfapi experience; only for Windows 11 modern context-menu entries, which v1 does not add (https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/integrate-packaged-app-with-file-explorer).
- **Cloud thumbnails, status flyout, custom states, Share handler, socket.io push.** P2 candidates (the settings page and bandwidth limits were moved into v1 on 2026-11-15).
- **"Shared with me" / "Recent" views.** The endpoints are deprecated and stop returning data after November 2026 (https://learn.microsoft.com/en-us/graph/api/drive-sharedwithme?view=graph-rest-1.0).
- **Replacing OneDrive automatically.** NoDrive runs side by side with the OneDrive app (decision 2026-11-16, ADR-0013). It never unlinks, uninstalls or reconfigures OneDrive; an optional assistant guides a manual switch.
- **Windows 10.** Windows 11 24H2 with the August 2025 cumulative update or later is the floor; Windows 10 22H2 under ESU shares the API baseline but is untested.
- **Personal Vault, OneNote packages, Windows-invalid names**: shown as unsupported, not synced (presentation decided in P1).
- **Microsoft.Graph SDK and MSAL/WAM broker**: not used in v1 (see decisions).

## Hard requirements

These are not features; a release that violates any of them does not ship.

1. **Never hang or block File Explorer.** No I/O inside a cfapi callback: validate, enqueue, return. Every pending request has a watchdog under the 60 s platform deadline, `CfReportProviderProgress` at least every 15 s while work continues, and a failing completion status when progress is impossible, so Explorer shows an error instead of freezing. (Slow callback work freezing Explorer is exactly what Nextcloud users saw in very large VFS folders, community-reported: https://help.nextcloud.com/t/windows-11-explorer-freez-with-nextcloud-client-33-0-0-to-35-0-0-fixed-with-35-0-1-update/243345.)
2. **Never leave a dialog stuck.** NOTIFY_DELETE / NOTIFY_RENAME are acknowledged from local state immediately; the remote operation is journaled and replayed asynchronously. A move of 10k placeholders completes in local-move time.
3. **Memory budget independent of item count.** Disk-backed index (SQLite via `winsqlite3.dll` or custom store), streaming delta and transfers with pooled fixed-size buffers, bounded queues and fixed concurrency (hydrations 3-4, uploads 2-3, delta ingestion 1), `HeapHardLimit`. Idle under 30 MB with 500k items; back to within 10 MB of idle within 60 s of a burst.
4. **500k items.** Lazy population only (never pre-create hundreds of thousands of placeholders), FETCH_PLACEHOLDERS answered from the index in batches, streamed and resumable initial delta, batched idempotent delta application, 410 Gone resync without ballooning memory.
5. **Correctness and no data loss.** Per-item state machine; If-Match eTag and explicit `conflictBehavior` on every write (upload sessions default to `fail`, simple PUT to `replace`: https://learn.microsoft.com/en-us/graph/api/resources/driveitem?view=graph-rest-1.0); conflict copies instead of overwrites; remote deletes to recycle bin only; never dehydrate a NotInSync file; mass-delete threshold pauses sync; journaled, idempotent, crash-safe mutations; double delete callbacks and duplicate delta entries harmless by design.
6. **Never fight the user over file handles.** Placeholder updates via `CfOpenFileWithOplock`; break own oplocks immediately when asked.
7. **Self-protection.** A supervisor watches callback latency, queue depth and commit; a request over budget is failed, logged, and the engine disconnects/reconnects cleanly rather than leaving Explorer waiting.
8. **Normal per-user desktop process.** Not a service (session-0 hydration blocked), not elevated, one connection per sync root, kept running (placeholders fail to open when the provider is down, community-reported; to verify in spike).

Acceptance tests for each item are listed in `projectPlan.md` and automated where possible.

## Confirmed decisions (2026-11-13)

| Decision | Choice |
|---|---|
| Explorer integration | cfapi placeholders, not a custom Explorer-like client (ADR-0001) |
| Runtime | .NET 11 (GA 2026-11-10, STS, support ends Nov 2028, same as .NET 10 LTS: https://devblogs.microsoft.com/dotnet/announcing-dotnet-11/) + Native AOT; fallback .NET 10 / framework-dependent trimmed JIT if the spike blocks (ADR-0002) |
| Auth | Hand-rolled auth-code + PKCE + system browser + loopback + DPAPI; MSAL/WAM deferred (ADR-0003) |
| Graph access | Raw `HttpClient` + System.Text.Json source generators, about 10 endpoints; no Microsoft.Graph SDK (ADR-0004) |
| Index | SQLite via system `winsqlite3.dll` (Microsoft.Data.Sqlite.Core + winsqlite3 provider); fallback raw P/Invoke to `winsqlite3.dll`, then a custom binary store (ADR-0005) |
| Licence | MIT; only permissive sources copied (rclone, CloudMirror, cfapiSync, WinCfApiWrapper, CfSharp, Vanara, CsWin32); GPL projects (Nextcloud, ownCloud, abraunegg/onedrive, onedriver, PrimalZed/CloudSync) read for protocol knowledge only (ADR-0006) |
| Scale | Design for 500k items: lazy population, disk-backed index, streaming delta (ADR-0007) |
| v1 scope | Browse, download, upload new files; no in-place editing (P2) |
| OneDrive.exe | Coexists (like a second computer); optional switch assistant; never touched by NoDrive (registry + SyncRootManager + `CfGetSyncRootInfoByPath` guard). Auto-detects the former OneDrive folder and offers it as the sync root. Adopts the files left there (metadata-only conversion on size/mtime match) in v1 as epic E1b, ADR-0008, confirmed 2026-11-13 |
| Packaging | Unpackaged in v1; registry-based shell integration; identity only if modern context-menu entries are ever wanted (P3) |
| Build/test host | Physical Windows 11 x64 PC; win-x64 primary target; optional GitHub Actions windows-latest (Native AOT cannot cross-compile from macOS) |
| Windows floor | Windows 11 24H2 with Aug 2025 cumulative update or later |
| Sync root | `%USERPROFILE%\NoDrive`, NTFS, never a drive root, added to the Search crawl scope |
| Hydration / population | FULL + AUTO_DEHYDRATION_ALLOWED (ADR-0010); population Full with the engine's own hybrid prefetch (ADR-0007) |
| Change wake-up | Adaptive delta polling in v1; socket.io channel in P2 |
| Photos / thumbnails | P2 unless the spike shows generic icons are unacceptable |
| Settings UI | Tray menu + browser sign-in + JSON config in v1; real window (Avalonia 12 or WinUI 3, separate process) later |

## Success criteria per phase

Detailed gates, kill criteria and acceptance tests are in `projectPlan.md`; summary:

- **P0 Spike (2-3 weeks, go/kill gate):** fresh Entra app + fresh personal account call `/me/drive/root/delta` without 403 `serviceReadOnly`; Native AOT hello/tray/HttpClient memory numbers recorded; unpackaged sync root with placeholders and progressive hydration under `PublishAot` shows nav-pane node, Status icons and built-in verbs on the target build; `CfUpdatePlaceholder` heap-corruption repro decided; `winsqlite3` publishes AOT-clean; 500k-item fake-server test gives first-sync time, index size, flat peak memory and sub-deadline FETCH_PLACEHOLDERS for a 30k-child folder; callback-latency histogram and fault-injecting fake Graph server exist.
- **P1 v1 (2-3 months):** all hard requirements pass their acceptance tests; manual Explorer checklist passes (open in Notepad and Photos, Office save, rename storm, Free up space, AV scan, engine killed mid-hydration, reboot); memory after a 1 GB hydration returns to within a few MB of idle; AOT publish with zero IL2xxx/IL3xxx warnings from our code.
- **P2:** in-place editing with conflict copies and USN replay, pin/unpin, thumbnails, status flyout, socket.io, settings window, each behind the same acceptance tests.
- **P3 (optional):** signed sparse package for modern context-menu entries; macOS/Linux exploration.

## Stakeholders

- **Single developer who is also the only user.** Knows C#, has .NET 11 installed, builds and tests on a physical Windows 11 x64 PC (planning done on macOS). Owns all decisions; there is no external customer, deadline or compatibility commitment.
- **AI assistants** working in this repo read the whole `memory_bank/` at session start and must honour this file's scope and hard requirements.

## Related files

- `productContext.md`: why NoDrive exists and how it should feel to use.
- `systemPatterns.md`: architecture and design rules that implement the hard requirements.
- `techContext.md`: stack, versions, licences, build commands.
- `projectPlan.md`: phases, gates, acceptance tests.
- `research/`: verified findings with sources (2026-11-13 research pass).
- `decisions/`: ADR-0001 to ADR-0007.
