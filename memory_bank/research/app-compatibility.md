# App compatibility: how Windows apps read cloud placeholders

Why it matters: with progressive hydration Windows calls our `FETCH_DATA` with the range the *calling app* needs. Cloud-aware apps read in small ranges (fast start, little download); others make Windows request the whole file at open, so the opener waits for the entire download. The engine cannot change this, but it can **measure it per app**, show the right UX (progress, cancel, tray hints) and document which apps to recommend. Process identity is available in every callback (`CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO`, `ImagePath`).

## Observed on the user's PC (Windows 11 26H2 build 26300.9974, 2026-11-14, spike log lines `required=[offset+length] optional=[offset+length]`)

| Caller (process) | What it did | Behaviour |
|---|---|---|
| `Photos.exe` (modern Photos app) | JPG 3-5 MB: required 4-64 KiB per request, optional range = rest of file; many sequential small requests, each answered in 1-2 s by the spike's unpooled client | **range-friendly** (starts after the first ranges; total download still completes in the background) |
| `msedge.exe` | image/PDF in the browser: required 4 KiB steps (`[0+4096]`, `[4096+4096]`, then the rest) | **range-friendly** |
| `explorer.exe` | thumbnail/preview handlers: whole small file (e.g. 145 KB PDF) | fine for small files; thumbnails of large online-only media would force big downloads (Windows only does it when it needs the thumbnail) |
| **`Microsoft.Media.Player.exe`** (Windows 11 Media Player, modern Store app, the default video player on a clean 26H2) | 1.42 GB mp4: `required=[0+1492094132] optional=[0+0]` at open; the hydration took 97.9 s | **whole-file at open**: playback starts only after the download (or after Windows decides enough data arrived); the opener waits |
| `powershell.exe` / .NET `File.Open`, raw `CreateFile` | `required=[0+whole file]` even for a 4 KB read at offset 0 or the middle; the wait is inside the open | whole-file at open (test tools, not typical apps) |
| `cfapi-minimal.exe` (our own pin via `CfHydratePlaceholder`) | `flags=0x2`, whole range | expected |

Correction recorded 2026-11-14: Media Player (`Microsoft.Media.Player.exe`) is the **new** Windows 11 app, not legacy Windows Media Player (`wmplayer.exe`); being modern does not imply range-friendly behaviour, so each player must be measured.

## To measure next (P1 diagnostic, E3d)

Open the same large online-only file (an mp4 of about 500 MB-1 GB) with each candidate and read the caller and ranges from the log: Films & TV (`Microsoft.ZuneVideo`/new "Movies & TV"), VLC, MPC-HC/MPC-BE, mpv, PotPlayer, Windows legacy WMP, Edge/Chrome `<video>`, Photos (video), Clipchamp; Office apps (Word/Excel/PowerPoint on large docs), Adobe Reader/Edge PDF, Notepad, 7-Zip/Explorer zip browsing, Visual Studio Code, antivirus (Defender scan hydrates whole files: expected `CF_CONNECT_FLAG_BLOCK_SELF_IMPLICIT_HYDRATION` does not cover other processes). Record per app: first-range size, whether `optional` is used, time to first frame/page, total bytes pulled before the user sees content.

## Engine features this implies (plan item E3d)

- Persistent **caller behaviour log** (local only): process image, requested range pattern, bytes pulled before cancel/close, time to first byte, per file extension. Shown by `NoDrive.Engine.exe --status --callers` and used to tune read-ahead per caller.
- **UX for whole-file callers**: native toast/progress already shown by Windows; the tray should show "downloading <name> for <app>" with a Cancel action; consider an optional prompt for files above a size threshold opened by a known whole-file app.
- **Per-caller read-ahead**: for range-friendly callers use the `optional` range sparingly (do not pull the rest of a 1 GB file until the user keeps reading); for whole-file callers there is nothing to save.
- A short **recommended apps** note for users (which video/PDF/image apps behave well with online-only files), generated from the measured table above.

## Measured 2026-11-15 (user's PC, hydration policy Full)

| App | Behaviour with online-only files | Result |
|---|---|---|
| Photos (packaged) | Partial reads, cancelled quickly under PROGRESSIVE | Corrupted image under PROGRESSIVE; works under Full (Downloading dialog first) |
| Paint, PDF viewers, Edge | Whole-file or sequential reads | Work under both policies |
| Media Player / Photos video | Whole file needed | Plays after the full download (like OneDrive) |
| explorer.exe thumbnails | Small reads, many files | Under Full no hydration is triggered; pictures show an icon until downloaded (cloud thumbnails are P2) |
| Windows Search, antivirus | Background reads | Served from a separate background lane with no read-ahead (progressive mode only) |
