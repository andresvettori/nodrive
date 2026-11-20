# P0 spikes

Feasibility and measurement apps for the P0 gate (see `memory_bank/projectPlan.md`). Each spike is a
small, throwaway project; results (numbers, screenshots, logs) are recorded in
`memory_bank/progress.md` and `memory_bank/research/`.

Planned spikes, in order:

1. `graph-smoke/` - **done 2026-11-13 (runs on any OS, no AOT)**: PKCE sign-in via browser + loopback, refresh grant, `/me/drive`, delta pages with `$top`, item downloadUrl, Range reads. Usage: `dotnet run --project spikes/graph-smoke -- [--pages N | --all | --largest N] [--top N] [--interactive]` (`--largest N` enumerates everything and prints the N biggest files with their item ids, for the burst test); env `NODRIVE_CLIENT_ID` overrides the embedded client ID. Saves the (rotating) refresh token to `~/.nodrive-spike/token.json` (mode 600); delete that file to sign out. Results in `memory_bank/research/graph-onedrive.md`.
2. `aot-baseline/` - **code ready (compiles on macOS; run on Windows)**: `--variant hello|tray|http [--download <url> | --item <driveItemId>] [--sample N]` (`--item` signs in and fetches a fresh downloadUrl at run time, since those expire after about an hour); hidden window + message loop, tray icon with Quit, streamed download + post-burst GC. Measure with `measure.ps1`.
3. `cfapi-minimal/` - **code ready (compiles on macOS; run on Windows)**: `[--root <path>] [--top N] [--workers N] [--win32-register] [--interactive] | --cleanup`; WinRT registration with Win32 fallback, FETCH_DATA/CANCEL/DELETE/RENAME callbacks (enqueue and return), placeholders for one delta page, Range hydration in 1 MiB chunks, callback-latency p50/p99/max on exit. Refuses foreign sync roots and non-empty folders.
4. `cfapi-heapcorrupt/` - reproduce the CfUpdatePlaceholder heap corruption on the dev build.
5. `sqlite-aot/` - winsqlite3 via Microsoft.Data.Sqlite.Core under PublishAot.
6. `scale-500k/` - fake Graph server emitting a 500k-item delta; first-sync time, index size, memory.
7. `responsiveness-harness/` - callback latency histogram + fault-injecting fake Graph server.

Scripts: `onedrive-state.ps1` (read-only report of OneDrive accounts, folder backup state, sync roots, policies), `measure.ps1` (memory/threads/handles sampler to CSV).

Windows only (except 1 and most of 6): run on the Windows 11 x64 PC following `memory_bank/p0-runbook.md`, which has the prerequisites, exact commands, manual Explorer checks and where to record results. `measure.ps1` samples a process's memory/threads/handles to CSV for the measurement table.
