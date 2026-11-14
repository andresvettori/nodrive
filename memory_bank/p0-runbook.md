# P0 runbook: what to run on the Windows 11 PC, in order

Operational companion to `projectPlan.md` section 2 (the plan says *what* each step must prove; this file says *how*, as PowerShell you can paste). Record results in `progress.md`, the table in `projectPlan.md` 2.3, and `research/`. Step 1 was done from macOS on 2026-11-13.

Legend: **[ready]** code is in the repo and compiles; **[to write]** still to be written.

All commands assume a non-elevated PowerShell 7 or Windows PowerShell session at the repo root.

**Which commands block, and how to stop them** (they are servers, not scripts):
- `aot-baseline.exe` and `cfapi-minimal.exe` run until you quit them: right-click their tray icon > **Quit**, or press **Ctrl+C** in their window, or `Stop-Process -Name aot-baseline` / `Stop-Process -Name cfapi-minimal` from another window. Start them with `Start-Process ...` (returns immediately, opens their own window) when you want to keep typing in the same window.
- `measure.ps1` runs for exactly `-Minutes N` (default 10) and then exits by itself.
- `graph-smoke --all` / `--largest` runs about 6 minutes and exits by itself; `--pages N` exits in seconds.
- `onedrive-state.ps1` exits immediately.

## Fast path: run the spikes without installing any SDK or build tools

Every green CI run (https://github.com/andresvettori/nodrive/actions, workflow "windows") publishes the
`spikes-win-x64` artifact: Native AOT builds of `aot-baseline.exe` and `cfapi-minimal.exe`, the
framework-dependent `graph-smoke`, the helper scripts and a copy of this runbook. Nothing else is needed
on the PC for steps 2 and 3 except Windows itself (and .NET 11 only if you want `graph-smoke`).

```powershell
# F.1 Download: open the latest green run (run #6 or later: earlier binaries had default GC settings), scroll to "Artifacts", click spikes-win-x64 (GitHub needs you signed in; the repo is private).
#     The browser saves spikes-win-x64.zip to Downloads. Then:
New-Item -ItemType Directory -Force $env:USERPROFILE\nodrive-spikes | Out-Null
Expand-Archive -Force "$env:USERPROFILE\Downloads\spikes-win-x64.zip" "$env:USERPROFILE\nodrive-spikes"
Set-Location $env:USERPROFILE\nodrive-spikes
Get-ChildItem -Recurse -File | Unblock-File          # removes Mark-of-the-Web so SmartScreen does not block the unsigned exes
Get-ChildItem -Recurse -Filter *.exe | Select-Object FullName, @{n='MB';e={[math]::Round($_.Length/1MB,2)}}

#     Alternative with the GitHub CLI (one-time: winget install --id GitHub.cli -e ; gh auth login):
#     gh run download --repo andresvettori/nodrive --name spikes-win-x64 --dir $env:USERPROFILE\nodrive-spikes

# F.2 What is OneDrive doing on this PC? (read-only)
.\onedrive-state.ps1

# F.3 Step 2 quick version: idle memory of the AOT tray process, sampled for 10 minutes
Start-Process .\aot-baseline\aot-baseline.exe -ArgumentList '--variant tray'
.\measure.ps1 -ProcessName aot-baseline -Minutes 10 -Out results\aot-tray.csv
Stop-Process -Name aot-baseline                         # or right-click the tray icon > Quit

#     Burst: 1 GB from your own drive (signs in through the browser the first time; token saved under %LOCALAPPDATA%\NoDrive\spike)
Start-Process .\aot-baseline\aot-baseline.exe -ArgumentList '--variant http --item F61C08DC8B03ABFF!952229 --limit-mb 1024'
.\measure.ps1 -ProcessName aot-baseline -Minutes 12 -IntervalSeconds 5 -Out results\aot-http-burst.csv
Stop-Process -Name aot-baseline

# F.4 Step 3 quick version: the Cloud Files provider. It BLOCKS and waits for Explorer activity; the console is the log.
Start-Process .\cfapi-minimal\cfapi-minimal.exe -ArgumentList '--top 200'      # own window; wait for "placeholders created"
explorer.exe "$env:USERPROFILE\NoDriveSpike"
#     Then, with the console visible, do these in Explorer and watch for [cb] / [hydrate] lines:
#       a) Look at the navigation pane: is there a "NoDrive Spike" node? Does the folder show a Status column with cloud icons?
#          (View > Details; right-click a column header > Status if hidden.)
#       b) Double-click a SMALL online-only file (a .pdf/.txt/.jpg). Expect: it opens; console shows "[hydrate] ... offset=0 length=..."
#          then "[hydrate] done"; the file's icon turns to a green check.
#       c) Right-click a file: are "Always keep on this device" / "Free up space" there (or inside a cloud-provider flyout)? Pin one
#          file -> expect a [hydrate] line without you opening it.
#       d) Rename a placeholder (F2) -> expect "[cb] NOTIFY_RENAME ... -> ack 0x00000000" immediately, no Explorer wait.
#       e) Delete a placeholder (Del) -> expect "[cb] NOTIFY_DELETE ... -> ack 0x00000000" immediately.
#       f) Open a large file (100 MB+) if the page has one, in a media player: watch several [hydrate] ranges arrive out of order.
#     Nothing in the spike writes to OneDrive: renames/deletes only affect the local spike folder."
.\measure.ps1 -ProcessName cfapi-minimal -Minutes 10 -Out results\cfapi-idle.csv
Get-WinEvent -LogName Microsoft-Windows-CloudFiles/Operational -MaxEvents 30 | Format-Table TimeCreated, Id, Message -Wrap
#     Quit from the tray icon (prints callback latency p50/p99/max), then remove the registration:
.\cfapi-minimal\cfapi-minimal.exe --cleanup
Remove-Item "$env:USERPROFILE\NoDriveSpike" -Recurse -Force

# F.5 If SmartScreen still shows "Windows protected your PC": click "More info" > "Run anyway" (the binaries are unsigned).
#     If Defender quarantines an exe, restore it and add an exclusion for %USERPROFILE%\nodrive-spikes; note it in results\.
```

Send the console output of both exes, the CSVs and screenshots of Explorer back; the full procedure with all checks is in sections 2 and 3 below.

## Remote test loop: drive the PC from the Mac over SSH

Windows 11 includes an OpenSSH server. With it enabled, the Mac (and Claude in this session) can run commands on the
PC directly and tail the spike logs, which turns each iteration from "download, run, paste" into seconds.

Caveat that shapes the scripts: a Cloud Files provider (and any tray icon) must run in the user's **interactive
desktop session**; an SSH session is a separate, non-interactive logon. `spikes/remote/start-in-session.ps1`
therefore launches the exe through a one-shot scheduled task bound to the interactive session, with the console
redirected to a log file. Reading a placeholder from the SSH session still triggers hydration (the request comes
from the file system, the provider answers from the desktop session), so hydration can be tested without touching
Explorer; the visual checks (nav pane, Status column, verbs) remain manual.

```powershell
# R.1 ONE TIME, on the PC, in an ELEVATED PowerShell (right-click > Run as administrator), from the repo clone:
cd C:\Projects\Personal\nodrive
git pull
Set-ExecutionPolicy -Scope Process Bypass -Force
.\spikes\remote\enable-ssh.ps1          # installs OpenSSH server, key-only auth with the Mac's key, firewall rule for the Private profile only
#   If it prints a network profile of "Public", switch the Wi-Fi/Ethernet to Private: Settings > Network & internet > (your network) > Private.
#   To undo later: .\spikes\remote\enable-ssh.ps1 -Disable

# R.1b Mac-side note: VS Code's own shell lacks the macOS "Local Network" permission (ssh says "No route to host" while Terminal/iTerm2 works).
#      Claude therefore runs remote commands through iTerm2 with spikes/remote/mac-pc.sh '<powershell>' and copies files with
#      spikes/remote/mac-put.sh <local-file> C:/path/on/pc (export NODRIVE_SCRATCH=<a writable dir> first). `git pull` over SSH on the PC
#      fails (HTTPS credential needs a UI), so use mac-put.sh to ship changed files, or pull interactively on the PC.

# R.2 From the Mac (Claude runs these):
ssh <PC_USER>@<PC_IP> 'hostname; $PSVersionTable.PSVersion.ToString()'

# R.3 Typical loop, from the Mac:
ssh <PC_USER>@<PC_IP> 'cd C:\Projects\Personal\nodrive; git pull; cd $env:USERPROFILE\nodrive-spikes'                  # or download the CI artifact
ssh <PC_USER>@<PC_IP> 'C:\Projects\Personal\nodrive\spikes\remote\start-in-session.ps1 -Exe $env:USERPROFILE\nodrive-spikes\cfapi-minimal\cfapi-minimal.exe -Arguments "--top 200" -Log $env:USERPROFILE\nodrive-spikes\results\cfapi.log'
ssh <PC_USER>@<PC_IP> 'Get-Content $env:USERPROFILE\nodrive-spikes\results\cfapi.log -Tail 40'
ssh <PC_USER>@<PC_IP> 'Get-Item $env:USERPROFILE\NoDriveSpike\<some file> | Get-Content -TotalCount 1 | Out-Null'   # triggers a hydration from the SSH side
ssh <PC_USER>@<PC_IP> 'Get-WinEvent -LogName Microsoft-Windows-CloudFiles/Operational -MaxEvents 20 | Format-Table TimeCreated, Id, Message -Wrap'
ssh <PC_USER>@<PC_IP> 'C:\Projects\Personal\nodrive\spikes\remote\start-in-session.ps1 -Stop'

# R.4 Copy result files back to the Mac:
scp -r <PC_USER>@<PC_IP>:nodrive-spikes/results ./results-pc
```

## 0. Prepare the PC (once, needed to build locally)

```powershell
# 0.1 Windows build and Cloud Files driver (need 24H2 = build 26100 with the Aug 2025 CU or later, cldflt.sys 10.0.26100.4946+)
$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
"$($cv.DisplayVersion) build $($cv.CurrentBuild).$($cv.UBR)"
(Get-Item "$env:SystemRoot\System32\drivers\cldflt.sys").VersionInfo.FileVersion

# 0.2 Tools (winget ids verified 2026-11-13 names; accept the prompts)
#     Shortcut for a first look without the C++ build tools: download the `spikes-win-x64` artifact from the latest
#     green run at https://github.com/andresvettori/nodrive/actions (ready AOT binaries for aot-baseline and cfapi-minimal).
winget install --id Microsoft.DotNet.SDK.11 -e
winget install --id Git.Git -e
winget install --id Microsoft.VisualStudio.2022.BuildTools -e --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
# optional
winget install --id Microsoft.PerfView -e
winget install --id Microsoft.Sysinternals.ProcessExplorer -e

# 0.3 Verify
dotnet --version        # 11.0.1xx
git --version

# 0.4 Get the code
git clone git@github.com:andresvettori/nodrive.git nodrive    # or https://github.com/andresvettori/nodrive.git
cd nodrive
dotnet build            # 0 warnings expected
dotnet test             # all green

# 0.5 Cloud Files diagnostics log
wevtutil sl Microsoft-Windows-CloudFiles/Operational /e:true
# later: Get-WinEvent -LogName Microsoft-Windows-CloudFiles/Operational -MaxEvents 50 | Format-Table TimeCreated, Id, Message -Wrap

# 0.6 What state is OneDrive in on this PC? (accounts, folders, folder backup yes/no, sync roots, policies)
.\spikes\onedrive-state.ps1

# 0.7 Results folder for CSVs and screenshots
New-Item -ItemType Directory -Force results | Out-Null
```

OneDrive may keep running during steps 2-5: the spikes use `%USERPROFILE%\NoDriveSpike`, never the OneDrive folder. Unlinking and the folder-backup migration (README) only happen before adoption tests in P1, and only if `onedrive-state.ps1` says a known folder is redirected.

## 1. Graph smoke **[ready, done on macOS 2026-11-13]**

```powershell
dotnet run --project spikes/graph-smoke -- --pages 3 --top 1000
```
Expected: `/me/drive` 200, pages of about 1,000-1,200 items, downloadUrl present, two `206 PartialContent` Range reads. The token is saved to `$env:LOCALAPPDATA\NoDrive\spike\token.json` (plain JSON, delete it when done: `Remove-Item $env:LOCALAPPDATA\NoDrive\spike\token.json`).

## 2. Native AOT memory baseline **[ready: `spikes/aot-baseline`]**

One exe, three variants: `hello` (hidden window + message loop), `tray` (adds a tray icon with a Quit menu), `http` (tray + streamed 1 GB download into the void, then the post-burst GC sequence). It prints its own stats every 30 s; `spikes/measure.ps1` samples from outside.

```powershell
# 2.1 Publish AOT and JIT builds
dotnet publish spikes/aot-baseline -c Release -r win-x64 -p:PublishAot=true  -o artifacts/aot-baseline
dotnet publish spikes/aot-baseline -c Release -r win-x64 -p:PublishAot=false --self-contained false -o artifacts/jit-baseline
Get-ChildItem artifacts/aot-baseline | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,2)}}

# 2.2 Idle: run a variant in one window, sample for 10 minutes from another (repeat for hello, tray; and the JIT tray)
Start-Process .\artifacts\aot-baseline\aot-baseline.exe -ArgumentList '--variant tray'
.\spikes\measure.ps1 -ProcessName aot-baseline -Minutes 10 -Out results\aot-tray.csv
Stop-Process -Name aot-baseline      # or Quit from the tray menu

Start-Process .\artifacts\aot-baseline\aot-baseline.exe -ArgumentList '--variant hello'
.\spikes\measure.ps1 -ProcessName aot-baseline -Minutes 10 -Out results\aot-hello.csv
Stop-Process -Name aot-baseline

Start-Process dotnet -ArgumentList '.\artifacts\jit-baseline\aot-baseline.dll --variant tray'
.\spikes\measure.ps1 -ProcessName aot-baseline -Minutes 10 -Out results\jit-tray.csv   # process name may be 'dotnet' for the JIT build
Stop-Process -Name aot-baseline, dotnet -ErrorAction SilentlyContinue

# 2.3 Burst: pick a file of about 1 GB from your own drive by id (graph-smoke lists the largest files; the
#     spike fetches a fresh downloadUrl at run time because those URLs expire after about an hour)
#     Already done from macOS on 2026-11-13 (full list in results of `graph-smoke --largest 15`); candidates on this drive:
#       F61C08DC8B03ABFF!952229                      Atari3gb.hd            3072 MB  (Documentos/ATARI/DISKIMG)   <- use with --limit-mb 1024
#       F61C08DC8B03ABFF!1059067                     20231112_213913.mp4    2912 MB  (Fotos/Samsung Gallery/DCIM/Camera)
#       F61C08DC8B03ABFF!s0ac401b3b63146d1956961f06e5b2bed  20241212_212810.mp4  4770 MB  (Fotos/Camera Roll)   <- full-length run
#     To refresh the list: dotnet run --project spikes/graph-smoke -- --largest 10   (about 6 minutes)
Start-Process .\artifacts\aot-baseline\aot-baseline.exe -ArgumentList '--variant http --item F61C08DC8B03ABFF!952229 --limit-mb 1024'
.\spikes\measure.ps1 -ProcessName aot-baseline -Minutes 12 -IntervalSeconds 5 -Out results\aot-http-burst.csv
Stop-Process -Name aot-baseline
#     (alternative without Graph: --download <any public 1 GB test URL>)

# 2.4 Reference rows on the same machine
.\spikes\measure.ps1 -ProcessName OneDrive -Minutes 10 -Out results\onedrive-idle.csv
Get-Process OneDrive | Select-Object Id, @{n='PrivateMB';e={[math]::Round($_.PrivateMemorySize64/1MB,1)}}, @{n='WSMB';e={[math]::Round($_.WorkingSet64/1MB,1)}}, Threads, Handles

# 2.5 Summarise a CSV (idle = median of samples after minute 2)
Import-Csv results\aot-tray.csv | Where-Object { [int]$_.elapsed_s -ge 120 } | Measure-Object private_bytes_mb -Average -Minimum -Maximum
```
Record in `projectPlan.md` 2.3: idle Private Bytes at 2 and 10 min, Working Set, threads, handles, peak during the download, Private Bytes 60 s after the burst, binary size. Pass criteria: AOT `tray` idles well under 30 MB and returns to within 10 MB of idle within 60 s after the burst.

## 3. Unpackaged sync root with placeholders and progressive hydration **[ready: `spikes/cfapi-minimal`]**

The go/no-go for the architecture. Under Native AOT the spike signs in, fetches one delta page (`--top 200`), registers `%USERPROFILE%\NoDriveSpike` with `StorageProviderSyncRootManager.Register` (falls back to `CfRegisterSyncRoot` and says so), connects with FETCH_DATA / CANCEL / NOTIFY_DELETE / NOTIFY_RENAME callbacks that only enqueue and return, pre-creates placeholders for that page, and hydrates on demand with Range downloads in 1 MiB chunks. It refuses to run inside a foreign sync root or a non-empty folder.

```powershell
# 3.1 Publish (record any IL2xxx/IL3xxx warnings and whether the WinRT projection compiled)
dotnet publish spikes/cfapi-minimal -c Release -r win-x64 -p:PublishAot=true -o artifacts/cfapi-minimal 2>&1 | Tee-Object results\cfapi-publish.log
Select-String -Path results\cfapi-publish.log -Pattern 'IL2','IL3','warning','error'

# 3.2 Run (keep this window open; it prints every callback and hydration)
.\artifacts\cfapi-minimal\cfapi-minimal.exe --top 200
#    if WinRT registration fails under AOT, the console says so and uses the Win32 path; to force it:
#    .\artifacts\cfapi-minimal\cfapi-minimal.exe --win32-register

# 3.3 In a second window: is it registered, and what does Explorer see?
Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager' | Where-Object PSChildName -like 'NoDriveSpike*' | Format-List
explorer.exe "$env:USERPROFILE\NoDriveSpike"
attrib "$env:USERPROFILE\NoDriveSpike\*"          # expect O (offline) / P (pinned) / U (unpinned) attributes on placeholders

# 3.4 Manual checks (screenshot each into results\): nav-pane node "NoDrive Spike"; Status column cloud icons; right-click shows
#     "Always keep on this device" / "Free up space" (or a provider flyout: note which); double-click a small file -> opens,
#     console shows [hydrate] lines with 206 ranges; open a 100 MB+ file in a media player (sequential) and an image in Photos
#     (memory-mapped) and note partial vs full hydration; pin a folder -> FETCH_DATA per file; rename and delete a placeholder ->
#     [cb] NOTIFY_RENAME / NOTIFY_DELETE acked instantly, Explorer never waits.

# 3.5 Provider-down behaviour
Stop-Process -Name cfapi-minimal
#   double-click an online-only file -> expect error 0x8007016A; then restart and retry -> success
.\artifacts\cfapi-minimal\cfapi-minimal.exe --top 200

# 3.6 Idle memory of the connected AOT provider
.\spikes\measure.ps1 -ProcessName cfapi-minimal -Minutes 10 -Out results\cfapi-idle.csv

# 3.7 Event log and callback latency (printed by the spike on exit: p50/p99/max microseconds)
Get-WinEvent -LogName Microsoft-Windows-CloudFiles/Operational -MaxEvents 50 | Format-Table TimeCreated, Id, Message -Wrap

# 3.8 Cleanup (unregisters; unhydrated placeholders in the spike folder are deleted by Windows, hydrated files remain)
.\artifacts\cfapi-minimal\cfapi-minimal.exe --cleanup
Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager' | Where-Object PSChildName -like 'NoDriveSpike*'   # expect nothing
Remove-Item "$env:USERPROFILE\NoDriveSpike" -Recurse -Force
```
Kill criteria (`projectPlan.md` 2.4): AOT callbacks or the WinRT projection do not work on net11.0 and the fallbacks also fail; or an unpackaged registration shows no nav-pane node / Status column on this build.

## 4. CfUpdatePlaceholder heap-corruption repro **[external repo]**

```powershell
git clone https://github.com/iterate-ch/CloudFiles-HeapCorrupt ..\CloudFiles-HeapCorrupt
Get-Content ..\CloudFiles-HeapCorrupt\README.md     # build/run per its instructions
# record: reproduces yes/no, build from step 0.1, cldflt.sys version
```
If it reproduces, the engine prefers `CfSetInSyncState`/`CfSetPinState` and limits `CfUpdatePlaceholder` to metadata-only updates behind an oplock.

## 5. SQLite via winsqlite3 under AOT **[to write: `spikes/sqlite-aot`]**

Console app referencing `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.provider.winsqlite3`, calling `SQLitePCL.raw.SetProvider(new SQLite3Provider_winsqlite3())`, creating the planned `items` table and inserting 500,000 synthetic rows in 5,000-row transactions, then querying children of one parent with 30,000 rows.
```powershell
dotnet publish spikes/sqlite-aot -c Release -r win-x64 -p:PublishAot=true -o artifacts/sqlite-aot 2>&1 | Tee-Object results\sqlite-publish.log
Select-String -Path results\sqlite-publish.log -Pattern 'IL2','IL3'
.\artifacts\sqlite-aot\sqlite-aot.exe
```
Record: warnings (which package), insert time, children-query time, DB size, peak Private Bytes. Fallback if not AOT-clean: raw `LibraryImport` to `winsqlite3.dll`.

## 6. 500k-item scale test **[to write; mostly macOS]**

Fake Graph server emitting 500,000 synthetic delta items, streamed into SQLite; on Windows add `FETCH_PLACEHOLDERS` for a 30,000-child folder answered from the index. Real-account reference from step 1: 136,411 items in 129 pages, 6.5 min, 3.0 s/page.

## 7. Responsiveness harness **[to write]**

Callback-latency histogram (the cfapi spike already prints p50/p99/max), pending-request age tracker, fault injection in the fake server (429 with long Retry-After, timeouts, expired downloadUrl, 200 instead of 206, network loss). Pass criteria: `projectPlan.md` section 6.

## 8. Write the go/no-go

Fill `projectPlan.md` 2.3 and 2.4, tick `progress.md`, answer every row in `projectPlan.md` section 8, update `activeContext.md`. If "go": start P1 epic E1.
