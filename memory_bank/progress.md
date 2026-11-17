# Progress

**Status 2026-11-16: P0 done (GO); P1 functionally complete for daily use on the author's PC.** Everything below marked
"verified" was exercised by the user on Windows 11 (Native AOT engine from CI unless noted). Open items are in "Not verified yet" and "Known issues".

## What works

- **Registration and Explorer integration** (unpackaged sync root, Flags 1058: Status column, cloud icons, keep/free verbs, nav-pane node; Windows creates the sidebar entry, it cannot join the top Home/Gallery/OneDrive group). Verified.
- **Browse**: lazy placeholders answered from the SQLite index; background prefetch for heavy folders; empty folders complete cleanly; first sync of about 90k items takes minutes and memory stays flat. Verified.
- **Download**: hydration policy Full (whole file at first access, Windows shows its own "Downloading" dialog); Photos, Paint, PDFs, videos open correctly; no-progress watchdog, throttle handling, failure memory, shared pooled HttpClient. Keep on device / Free up space with Explorer refresh. Verified.
- **Remote changes**: delta ingestion with adaptive polling (E4) and mirroring into folders already on disk: create, rename/move (marked in sync), delete (placeholders only), update (dehydrate); Explorer refreshes without F5 (E4b). Verified.
- **Upload (E5)**: watcher + journal, 5 s quiet period, two lanes, single PUT up to 4 MiB else resumable 5 MiB chunks, conflict copies on name clash, conversion to in-sync placeholders; identical existing files and folders are linked instead of uploaded (name + size + mtime). Verified, including reinstall adoption (0 conflict copies, thousands linked in seconds).
- **Delete/rename from Explorer (E6)**: acked at once, journalled (`remote_ops`), replayed in order with backoff; delete uses If-Match and the recycle bin; rename conflicts get a conflict name; mass-delete guard (200 per 10 min) with a tray "Allow" action. Verified.
- **Tray and lifecycle (E7)**: menu (open page, pause/resume persisted, autostart, sign-in, allow deletes, quit), single instance (per-user global mutex), quit event, crash restart (max 3 in 10 min), recoverable sign-in. Verified.
- **Local settings and activity page**: live transfers with progress, persistent recent activity with selectable last-N, speed tiles, settings with a header save bar, bandwidth limits (download limit verified), EN/ES, Windows regional formats and time zone, guides, OneDrive status card. Verified (upload limit and Spanish text partly).
- **Installer and uninstall (E8, part)**: WiX v5 per-user MSI built in CI (artifact `NoDrive-installer-win-x64`); install, upgrade, uninstall and reinstall verified; engine commands `--quit`, `--pending`, `--uninstall`; damaged index is rebuilt.
- **Switch from OneDrive assistant** (optional): read-only scan verified on the real OneDrive folder (identical / online-only / different / only-on-PC); take-over step built, not yet run.
- **Quality**: 48 unit tests (index, delta, population, hydration policy, read-ahead, Graph with fake handler, upload and ops journals, bandwidth limiter); CI builds, tests and AOT-publishes with zero IL2xxx/IL3xxx warnings; engine idle 10-15 MB private, 15-30 MB under work.

## Not verified yet

- Upload bandwidth limit with a real upload; Spanish text across all pages.
- Take-over step of the Switch assistant (needs OneDrive unlinked); behaviour while adoption is linking a very large tree (folders answer "unavailable" until it finishes).
- Installer wizard wording and the message shown when uninstall is refused (generic MSI error, exit code 3 from `--uninstall`).
- Native AOT soak (24 h), automated responsiveness harness in CI, manual Explorer checklist (Office save, rename storm, AV scan, kill mid-hydration, reboot).

## P0 summary (done 2026-11-14, verdict GO; details in `p0-results.md`)

| Question | Result |
|---|---|
| Graph on a personal account | PKCE, refresh grant, delta, Range 206 all work; no 403 serviceReadOnly; 136k items enumerated in 6.5 min |
| AOT footprint | tray idle 4.3 MB private; cfapi provider idle 12.7-14.6 MB, +2.6 MB during a 1.68 GB hydration; OneDrive.exe idle 209 MB |
| Unpackaged cfapi under AOT | Register, callbacks, hydration, pin/unpin, rename/delete acks all work; zero IL warnings |
| CfUpdatePlaceholder heap corruption | not reproduced on build 26300.9974 (14,000 calls); keep `--heap-repro` as a regression check |
| SQLite via winsqlite3 under AOT | works; 500k rows, 60.7 s ingest, peak 33 MB, 10.3 MB after release, 154 MB DB |
| Explorer responsiveness | 31k-child folder froze Explorer 7-10 s on demand; fixed by background prefetch; fault-injection harness: errors in under 0.2-15 s, probes under 25 ms |

## P1 checklist (mirrors `projectPlan.md`)

- [x] E1 Registration and placeholders (policy Full, population Full + prefetch; never unregister on exit; sync root moves are handled)
- [~] E1b OneDrive coexistence: allowed (side by side); detection card, guide and the optional switch assistant done; take-over unverified; no OneDrive.exe refusal by design
- [x] E2 Lazy enumeration from the index only
- [x] E3 Hydration (Full policy; Range with 200 fallback kept; progress; watchdog; cancel handling; read-ahead only used by the progressive mode)
- [x] E3b Pin/unpin handling; [x] E3c Background population
- [~] E3d Caller diagnostics: caller stats in the log report and the activity page; no dedicated UX
- [x] E4 Delta ingestion and index (+ E4b mirror into populated folders)
- [x] E5 Upload of new files and folders (+ adoption of identical files)
- [x] E6 Delete/rename propagation with guards
- [x] E7 Tray, pause, autostart, single instance, crash restart; [x] E7a Setup flags 1058
- [~] E8 Recovery and uninstall: journals replayed, index rebuild, MSI uninstall with pending-work check; USN cursor reconcile not done
- [~] E9 Memory hygiene: GC knobs in `eng/LowFootprint.props`, bounded queues and buffers, measured 15-30 MB; no explicit post-burst aggressive collect
- [~] E10 Responsiveness harness: PowerShell harness from P0 exists; CI harness and 24 h soak not done
- [x] Installer (WiX MSI) and CI artifacts; [ ] code signing
- [ ] Manual Explorer checklist complete; [ ] v1 tagged

### P1.5 (added 2026-11-15)
- [x] App icon (tray, folder, installer), local settings/activity page (EN/ES), persistent activity journal, bandwidth limits, OneDrive status card, guides in `docs/`
- [~] Switch from OneDrive assistant (scan verified, take-over pending a real run)

### P2
- [ ] Editing existing files: If-Match uploads, 412 conflict copies, echo suppression, Office save-storm debounce, USN replay (first item: changes to existing files are not uploaded today)
- [ ] Cloud thumbnails for online-only pictures (thumbnail COM server backed by Graph `/thumbnails`)
- [ ] Known Folder Move support for Desktop/Documents/Pictures (only guidance and detection exist)
- [ ] StatusUI flyout; socket.io wake-up; metered-network and power events; parallel segment download (single connection is about 14 MB/s); tempauth URL retirement readiness (2027-04-01); MSIX/winget packaging once code signing exists

### P3 (optional)
- [ ] Signed sparse package for modern context-menu entries; Explorer Home integration; macOS (File Provider) / Linux (the web page is already portable)

## Known issues

- **Reinstall adoption bug (fixed 2026-11-16, to be verified by the user):** after uninstall and reinstall some folders were empty or missing cloud-only files; see `activeContext.md`. A repair walk runs once on the next start.
- **Edits to files already in the cloud are not uploaded** (v1 scope; P2). Local edits stay local.
- **Online-only pictures show no preview thumbnail**; Explorer does not hydrate for thumbnails under the Full policy (by design, no bandwidth burst). Cloud thumbnails are P2.
- **Large videos download completely before they play** (same as OneDrive). The earlier progressive-streaming problem (choppy Photos playback, hang on pause) is moot under Full and the progressive mode is kept only as a switch.
- **Navigation pane**: NoDrive stays in the lower group; the top group is reserved for the OneDrive known folder (`research/explorer-ux.md`).
- **Unsigned binaries** can be blocked by Windows Application Control / Smart App Control (seen as 0x800711C7 on freshly built JIT DLLs). The CI AOT exe and MSI run fine on the author's PC; wider distribution needs signing.
- **Uninstall while work is queued** aborts with a generic Windows Installer error (the reason is in the log and `--pending`).
- A single download connection reaches about 14 MB/s; parallel segment download is a P2 idea.
- Adoption matches files by name, size and modified time (no hash yet); a changed file with identical size and mtime would be linked, not uploaded.

## Platform and toolchain risks (still valid; see `projectPlan.md` section 7 for mitigations)

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
| 2026-11-14 | Hybrid population: background prefetch of heavy folders (about 1,875+ children) plus on-demand fallback | On-demand costs about 160 us per placeholder inside Windows and froze Explorer 7-10 s on a 31k-child folder; measured, not tunable | ADR-0007 (amended) |
| 2026-11-14 | Sync-root Flags 1058 written after Register, without elevation | Gives the OneDrive-like Status column, icons and verbs; re-registering resets it, so never re-register an existing root | ADR-0009 |
| 2026-11-14 | Coexistence with OneDrive.exe allowed; adoption by name + size + mtime instead of refusing to start | User keeps OneDrive installed; two clients on one account behave like two computers | ADR-0008 (amended), ADR-0013 |
| 2026-11-15 | Hydration policy FULL (whole file on first access) instead of PROGRESSIVE | Photos cancels slow partial reads and calls JPGs corrupted; OneDrive behaves the same way (Downloading dialog); thumbnails do not hydrate | ADR-0010 |
| 2026-11-15 | Settings and activity UI is a local web page served by the engine, not WinForms/Avalonia | Zero UI framework in the always-on AOT process, same page reusable on macOS/Linux, EN/ES, regional formats from the browser | ADR-0011 |
| 2026-11-15 | Bandwidth limits via a shared virtual-time limiter (Core), live-configurable | OneDrive parity; works for downloads and uploads without extra threads | plan P1.5 |
| 2026-11-15 | Remote changes are mirrored into populated folders; Explorer deletes/renames are acked at once and journalled (no more read-only) | Delete/rename acks must never wait for the network; journal makes replay idempotent after crashes | plan E4b, E6 |
| 2026-11-16 | Installer = WiX v5.0.2 per-user MSI (not Inno Setup, not MSIX yet) | User's choice; v6+ carries the maintenance-fee EULA; MSIX needs a signing certificate | ADR-0012 |
| 2026-11-16 | Optional "Switch from OneDrive" assistant; the installer stays side-by-side only | User approval; never unlinks, uninstalls or changes Folder Backup itself | plan P1.5 |
