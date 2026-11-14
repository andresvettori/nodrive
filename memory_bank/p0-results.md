# P0 results and go/no-go (written 2026-11-14)

Evidence gathered on the user's personal Windows 11 PC (build 26300.9974, "26H2", cldflt.sys 10.0.26100.9974, OneDrive.exe running, Windows Search disabled) with a real personal OneDrive (95-136k items) plus a synthetic 500,000-item drive. Everything below is measured, with the detail in `research/` and `progress.md`. Spikes: `graph-smoke`, `aot-baseline`, `cfapi-minimal`, `scale-500k`.

## Verdict: **GO** for P1, with the design changes listed at the end

No kill criterion of `projectPlan.md` 2.4 fired. The two that came close (no Status column/verbs; Explorer freeze on large folders) were both resolved with measured fixes.

## Scorecard against the requirements and kill criteria

| Requirement / kill criterion | Target | Measured | Verdict |
|---|---|---|---|
| Graph auth + delta on a personal account, no 403 | works | PKCE + refresh work, delta 95k-136k items, Range 206, no throttling | pass |
| Idle memory of the resident process | < 30 MB | tray AOT 4.3 MB private / 14.5 MB WS (20 min); cfapi provider 10-15 MB; OneDrive.exe 209 MB on the same PC | pass (7-20x better than OneDrive) |
| Memory returns after a burst | within 10 MB of idle in 60 s | 1 GB HTTP burst peak 9.0 -> 6.4 MB after release GC; 1.68 GB pin hydration +2.6 MB, +2.5 MB at +60 s | pass |
| Memory independent of item count (500k) | flat | 500k items ingested: peak 33 MB, 10.3 MB settled; 50k vs 500k peaks within 2 MB | pass |
| AOT toolchain (WinRT projection, cfapi callbacks, SQLite, STJ) on .NET 11 | zero IL2xxx/IL3xxx | all spikes publish AOT clean (cfapi 6.2 MB, scale 5.3 MB); AOT binaries verified on the PC | pass |
| Unpackaged provider shows nav pane / Status / icons | works | works once Flags=1058 is written after Register (no elevation; Users may write the key); verbs, icons, Status identical to OneDrive; native download toast | pass (with ADR-0009) |
| Hydration correct and bounded | data identical | SHA-256/size equal to cloud; pin/unpin work (2.4 s / 17 ms); not-in-sync dehydrate refused by Windows | pass |
| Explorer never blocked by callbacks | microseconds | callbacks 0.1-5 ms; rename/delete acks 8-11 ms; open of an online-only file with the provider dead fails in 17 ms | pass |
| Explorer never blocked by population | no freeze | on-demand 31k-child folder froze the window 7-10 s (user-observed); not tunable (batch/parallel matrix); background prefetch fixes it (1.1 s open, 0 callbacks, foreground p95 unaffected) | pass with E3c |
| Faults do not hang anything | bounded, visible errors | throttling error in 0.18 s; stall error at 15.2 s; reset recovered by the platform retry; kill -> error in 0.2 s, restart recovers; 4 concurrent hydrations OK; probes median < 5 ms, max 25 ms | pass |
| 500k items: listing a huge folder | fast | 30,030-child folder from SQLite in 153 ms; 100k lookups 16 us each; first-sync ingest 61 s local (network-bound in reality) | pass |
| CfUpdatePlaceholder heap corruption (24H2/25H2 report) | no crash | 14,000 calls, 3 variants, heap valid, no events | not reproduced |

## What the P0 changed in the design (carry into P1)

1. **Registration and Explorer integration (ADR-0009):** `Register` once, then write `SyncRootManager\<id>\Flags = 1058` unelevated; never call `Register` on an existing root (it resets Flags to 34); engine verifies/repairs Flags at start-up. WinRT population policy has only Full/AlwaysFull.
2. **Population is hybrid (ADR-0007 amendment, E3c):** background prefetch of heavy folders (children >= about 2,000 = 300 ms at 160 us each), with ancestor chain and a budget; FETCH_PLACEHOLDERS from the index stays as fallback; cost is Windows-internal (batch size and parallelism do not matter); `DISABLE_ON_DEMAND_POPULATION` goes with the last real batch or via `CfUpdatePlaceholder`.
3. **Hydration needs a watchdog and failure policy (new, from step 7):** no-progress timeout 15 s (not 60), Retry-After over 5 s fails the request immediately, a 30 s failure memory for no-progress/throttle/404 failures only (not for dropped connections: Windows re-requests the missing range and it succeeds), one shared `HttpClient` (the per-request client cost 1.5 s on small ranges), Windows re-issues failed requests so a stall costs one watchdog period not two.
4. **Windows requests whole files at open for some callers** (Media Player, PowerShell/Win32 opens: `required=[0+size]`; Photos asks for small ranges): opening a large never-hydrated video waits for the full download (12-14 MB/s on this Wi-Fi). Mitigation is product behaviour (progress UI/toast already native, cancel via CANCEL_FETCH_DATA), not engine tuning.
5. **Pin/unpin is the provider's job:** watch attributes (recursive `FileSystemWatcher`), hydrate with `CfHydratePlaceholder`, dehydrate with `CfDehydratePlaceholder` (works although the doc page is archived); sweep at start-up for pins made while the engine was off (E3b).
6. **Index (ADR-0005 validated):** SQLite over system `winsqlite3.dll`, AOT clean, 323 B/row, WAL + 4 MB page cache + one transaction per delta page with the resume token inside it.
7. **Operational facts:** the engine must be the *connected* provider to manage placeholders (`0x8007018B` otherwise); `CfUnregisterSyncRoot` deletes unhydrated placeholders (uninstall must warn/hydrate); a renamed placeholder is "not in sync" and Windows refuses to dehydrate it; OneDrive's own sync root coexists under the same user; consumer Graph returns `parentReference.path` and all of quickXor/sha1/sha256.

## Risks that remain open (not blockers)

- **Windows-version dependence:** the Flags value 1058 and several behaviours are undocumented or only community-documented; every behaviour was measured on build 26300.9974 only. Mitigation: start-up self-check, keep the spike regression scripts (`--heap-repro`, `populate-matrix.ps1`, `responsiveness.ps1`) and run them on each supported build.
- **Not yet tested:** a real multi-hour soak; the 4 GB-class real files (largest tested hydration 1.68 GB, 121 s); sleep/resume and network change events; Explorer behaviour with a 300k-child folder; two Windows users; BitLocker/AV interactions beyond Defender defaults; the upload path (P1 v1 scope) and delta apply to *populated* folders.
- **Graph consumer unknowns:** throttling thresholds unpublished, the 2027-04-01 pre-authenticated URL retirement, `socketIo` notifications untested.
- **User-environment:** OneDrive "Back up folders" is ON for Desktop/Documentos/Fotos (localised); the adoption epic E1b needs the migration procedure before first real use.

## Recommended P1 order (changes from the original plan marked *)

E1 registration and Flags (ADR-0009)* -> E4 delta ingestion + index (`scale-500k` code is the starting point) -> E2 lazy enumeration + E3c background population* -> E3 hydration with watchdog/failure policy* -> E3b pin/unpin -> E6 rename/delete propagation -> E5 upload new files -> E7 tray/lifecycle -> E1b adoption -> E8 recovery/uninstall -> E9/E10 memory hygiene and the responsiveness harness in CI (reuse `responsiveness.ps1`).

## Artifacts to keep

`spikes/` (all), `memory_bank/research/*`, `memory_bank/decisions/ADR-0001..0009`, the `ci-binaries` branch mechanism, and the PC-side kit (`spikes/remote/*`: SSH enablement, in-session launcher, measurement, matrix, responsiveness, heap repro, state report).
