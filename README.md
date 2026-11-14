# NoDrive

A deliberately small OneDrive client for Windows 11.

- One provider (personal OneDrive), one platform (Windows), one job: make your OneDrive files appear
  in File Explorer as on-demand placeholders, download on open, upload on drop.
- Built on the Windows Cloud Files API (the same platform OneDrive uses), so Explorer shows the
  status icons, hydration progress and "Always keep on this device / Free up space" verbs natively.
- Written in C# on .NET 11, published as a Native AOT, headless process with a tray icon. Designed
  for minimal idle memory that does not grow with the number of files, and to never block Explorer.

**Status: pre-alpha, P0 feasibility spike.** The solution builds and the unit tests pass, but the
engine's entry point is still a stub: nothing registers a sync root or talks to OneDrive yet. Sections
below are tagged **[now]** (works today) or **[planned]** (designed, not implemented; see
[memory_bank/projectPlan.md](memory_bank/projectPlan.md)). Progress is tracked in
[memory_bank/progress.md](memory_bank/progress.md).

## Documentation

All project knowledge lives in [`memory_bank/`](memory_bank/): brief, product context, architecture,
tech stack, plan, decisions (ADRs) and research notes with sources. Start with
[projectbrief.md](memory_bank/projectbrief.md). The architecture is in
[systemPatterns.md](memory_bank/systemPatterns.md); build details and toolchain quirks are in
[techContext.md](memory_bank/techContext.md).

## Prerequisites

| Purpose | Requirement |
|---|---|
| Build and run unit tests **[now]** | .NET SDK 11.0.100 or later 11.0.x (`global.json`), on Windows, macOS or Linux |
| Run or Native AOT publish the engine **[now]** | Windows 11 x64, build 24H2 (26100) with the August 2025 cumulative update or later; Visual Studio 2022/2026 "Desktop development with C++" workload (Native AOT needs the MSVC linker) |
| Use NoDrive against your OneDrive **[planned]** | A personal Microsoft account; an Entra app registration (see Configure); NTFS volume for the sync folder; OneDrive.exe unlinked from that account (see "Coexistence with OneDrive.exe") |

Windows-targeted projects restore and compile on macOS/Linux thanks to `EnableWindowsTargeting`, which
is how the engine is kept warning-free from a non-Windows dev box, but they only run on Windows.

## Build **[now]**

```powershell
git clone <repo-url> nodrive
cd nodrive
dotnet build                 # whole solution, Debug; 0 warnings expected (warnings are errors)
dotnet build -c Release
```

Projects:

| Project | Target | Role |
|---|---|---|
| `src/NoDrive.Core` | net11.0 | Reconciler, item state machine, index/journal abstractions. Platform neutral. |
| `src/NoDrive.Graph` | net11.0 | Hand-written Microsoft Graph client: PKCE auth, delta, download, upload sessions, quickXorHash. |
| `src/NoDrive.Engine` | net11.0-windows10.0.26100.0, WinExe | The always-on process: tray icon, Cloud Files provider, local watcher, supervisor. Native AOT. |
| `tests/NoDrive.Core.Tests` | net11.0 | xunit.v3 on Microsoft.Testing.Platform. Runs anywhere. |

## Test **[now]**

```powershell
dotnet test                          # all test projects
dotnet test --filter-class "*ScaffoldTests*"
```

The test runner opt-in (`"test": { "runner": "Microsoft.Testing.Platform" }`) lives in `global.json`.
Windows-only integration tests against Explorer are planned for `tests/NoDrive.Engine.Tests` and
run only on the Windows PC / CI runner.

## Publish the engine (Native AOT, Windows only) **[now]**

```powershell
dotnet publish src/NoDrive.Engine -c Release -r win-x64 -p:PublishAot=true -o artifacts/engine-win-x64
dotnet publish src/NoDrive.Engine -c Release -r win-arm64 -p:PublishAot=true -o artifacts/engine-win-arm64
```

Output is a single `NoDrive.Engine.exe` plus `.pdb`. The publish must emit no `IL2xxx`/`IL3xxx`
warnings from our code. Fallbacks if AOT is blocked on a machine (see ADR-0002): framework-dependent
trimmed JIT with `-p:PublishAot=false -p:PublishTrimmed=true`.

The GitHub Actions workflow `.github/workflows/windows.yml` runs build, tests and the win-x64 AOT
publish on `windows-latest` and uploads the binary as an artifact.

## Try the P0 spikes without installing anything **[now]**

Each green run of the `windows` workflow publishes a `spikes-win-x64` artifact with Native AOT builds of the
P0 spikes plus the helper scripts. Download it from the Actions tab (sign-in required, private repo), then:

```powershell
Expand-Archive -Force "$env:USERPROFILE\Downloads\spikes-win-x64.zip" "$env:USERPROFILE\nodrive-spikes"
Set-Location $env:USERPROFILE\nodrive-spikes
Get-ChildItem -Recurse -File | Unblock-File
.\onedrive-state.ps1                                    # what OneDrive is doing on this PC
.\aot-baseline\aot-baseline.exe --variant tray          # BLOCKS until you quit it (tray icon > Quit, or Ctrl+C); prints stats every 30 s
.\cfapi-minimal\cfapi-minimal.exe --top 200             # BLOCKS and logs callbacks while you use Explorer; tray > Quit; --cleanup to unregister
```

The full procedure, measurements and cleanup are in
[memory_bank/p0-runbook.md](memory_bank/p0-runbook.md), section "Fast path".

## Configure **[planned]**

NoDrive is configured once, at first run, and keeps its state under `%LOCALAPPDATA%\NoDrive\`.

### 1. Entra app registration: who does it and when

NoDrive signs in with OAuth 2.0 authorization code + PKCE through your default browser. It is a
**public client**: an app registration gives it a client ID and no secret, and that client ID is
meant to be embedded in the shipped binary. Three situations:

| You are | What you do |
|---|---|
| **Using a release build of NoDrive** | Nothing. The build embeds NoDrive's client ID. Sign in, consent to `Files.ReadWrite` and `offline_access`, done. Optionally set your own `clientId` in `config.json` to get your own throttling budget (see below). |
| **Building from source for yourself** | Register an app once (steps below) in your own tenant and put its client ID in `config.json`. This is the developer registration used during the P0 spike. |
| **Publishing NoDrive to others** | Your developer registration becomes the embedded default. Add branding (name, logo, publisher domain, privacy and terms URLs) and consider Microsoft publisher verification so the consent screen does not say "unverified". No per-user step is ever required. |

Why a user might still want their own client ID: Microsoft Graph throttles per application as well as
per user, so every installation sharing one embedded ID draws from one app-level budget. rclone
documents the same effect with its shared OneDrive ID and recommends power users register their own.
NoDrive keeps `clientId` overridable for that reason.

Security note: because there is no client secret, any program could present NoDrive's client ID to
ask a user for consent. That is accepted practice for desktop public clients; PKCE prevents an
authorization code from being stolen in flight, and the consent screen always shows the permissions.

Steps to register (publisher or self-builder, one time):

1. A personal Microsoft account cannot create app registrations by itself. Create a free Entra
   tenant (free Azure sign-up) and open **Microsoft Entra admin center > App registrations > New**.
2. Name: `NoDrive`. Supported account types: **Accounts in any organizational directory and personal
   Microsoft accounts** (`AzureADandPersonalMicrosoftAccount`).
3. Platform: **Mobile and desktop applications**, redirect URI `http://localhost` (port is ignored).
   Under Authentication set **Allow public client flows: Yes**.
4. API permissions (delegated, Microsoft Graph): `Files.ReadWrite`, `offline_access`. Nothing else.
   No admin consent is involved for personal accounts.
5. Copy the **Application (client) ID** into `config.json` (below), or into the build's embedded
   default if you are publishing. There is no client secret.

### 2. `config.json`

Location: `%LOCALAPPDATA%\NoDrive\config.json`. Created with defaults on first run; edit while the
engine is stopped.

```jsonc
{
  "clientId": "6711e7c6-ec09-42df-a5bb-bfb449119fdc",   // default embedded ID (the project's registration); replace with your own if you want a separate throttling budget
  "syncRoot": "C:\\Users\\you\\NoDrive",                // NTFS, not a drive root, not inside another sync root
  "adoptExistingFiles": true,                            // reuse files already present in syncRoot (see Coexistence)
  "poll": { "activeSeconds": 60, "idleMinSeconds": 900, "idleMaxSeconds": 3600 },
  "transfers": { "maxHydrations": 3, "maxUploads": 2, "chunkBytes": 10485760 },
  "safety": { "massDeleteThreshold": 0.20, "massDeleteMinItems": 100 },
  "memory": { "heapHardLimitBytes": 134217728 },
  "startWithWindows": true
}
```

Tokens (refresh token, account id) are stored DPAPI-encrypted in `%LOCALAPPDATA%\NoDrive\token.bin`;
the item index and journal in `%LOCALAPPDATA%\NoDrive\index.db` (SQLite, system `winsqlite3.dll`);
logs in `%LOCALAPPDATA%\NoDrive\logs\`.

## Run **[planned]**

```powershell
# first run: sign in and register the sync root
NoDrive.Engine.exe

# useful switches
NoDrive.Engine.exe --status          # prints account, sync root, pending work, memory; exits
NoDrive.Engine.exe --sign-out        # removes tokens, keeps files
NoDrive.Engine.exe --uninstall       # disconnect, offer to hydrate everything, unregister, remove autostart
NoDrive.Engine.exe --no-autostart    # do not add the HKCU Run entry
```

First run, step by step:

1. The engine checks for a conflicting OneDrive.exe (see Coexistence). If a personal account is still
   linked it shows a tray message and exits.
2. Your browser opens the Microsoft sign-in page; consent to `Files.ReadWrite` and `offline_access`.
3. The sync root (`%USERPROFILE%\NoDrive` by default) is registered with Windows. A **NoDrive** node
   appears in the Explorer navigation pane with your OneDrive's folders as online-only placeholders.
   Folders are populated lazily when you open them; nothing is downloaded until you open a file.
4. The tray icon shows state (idle, syncing, paused, error) and offers Pause, Open folder,
   Free up space / Keep on this device for a folder, Status, Sign out, Quit.
5. Autostart is an `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry; a Task Scheduler
   "restart on failure" task supervises the process. Online-only files cannot be opened while the
   engine is not running (Windows reports error 0x8007016A), so leave it running.

Diagnostics: `Microsoft-Windows-CloudFiles/Operational` in Event Viewer, plus `logs\`.

## Coexistence with OneDrive.exe **[planned]**

NoDrive is a replacement client for one personal account, not an add-on. See
[ADR-0008](memory_bank/decisions/ADR-0008.md) for the design and evidence.

- **Detection.** At start-up NoDrive reads `HKCU\Software\Microsoft\OneDrive\Accounts\Personal`
  (`UserFolder`, `UserEmail`), scans the Windows sync-root registry for an `OneDrive!<SID>!Personal`
  root, and asks the Cloud Files API whether the chosen folder already belongs to another provider.
  If a personal account is linked, NoDrive refuses to start and tells you to unlink it (OneDrive
  settings > Account > Unlink this PC). A running OneDrive.exe with only a work/school account, or
  with no account, is allowed. NoDrive never stops or modifies OneDrive itself.
- **What unlink leaves behind.** Windows keeps your locally available files as ordinary files and
  removes online-only placeholders from disk (the cloud copies are untouched). That is why adoption
  below is cheap: there is nothing to migrate except real files.
- **Folder auto-detection.** The former OneDrive folder (from `UserFolder` or `%OneDriveConsumer%`)
  is offered as the default sync root on first run.
- **Reuse (adoption).** With `adoptExistingFiles: true`, NoDrive registers its own sync root on that
  folder and adopts what OneDrive left behind: a file whose size and last-modified time match the
  cloud item is converted in place to an in-sync placeholder (no download), mismatching files are
  kept as `<name> (local copy YYYY-MM-DD)` beside a fresh placeholder, and files that do not exist
  in the cloud are uploaded (v1 upload-new). Adopted files are hash-verified in the background and
  are never dehydrated until verified. If OneDrive's **Back up folders** feature is on, follow the
  migration steps below first; adoption stops with a message if Desktop, Documents or Pictures are
  still redirected into the folder.
- **Keeping OneDrive from coming back.** Windows may reinstall or restart OneDrive after updates.
  After unlinking, consider the policy value `DisableFileSyncNGSC = 1` under
  `HKLM\Software\Policies\Microsoft\Windows\OneDrive` (documented for Pro/Enterprise; reported to
  work on Home) and removing the `OneDrive` entry from `HKCU\...\CurrentVersion\Run`.
  `NoDrive.Engine.exe --status` reports both.

### Migrating from OneDrive when "Back up folders" is on

**First check whether it is actually on.** Run `.\spikes\onedrive-state.ps1`; it prints each known
folder as `default` or `REDIRECTED`. If the script shows no redirected folder, backup is off and this
section does not apply. Where the setting lives in the current client: OneDrive cloud icon in the
notification area > gear > **Settings** > **Sync and backup** tab > **Manage backup** button (older builds:
a "Backup" tab). Windows 11 also exposes it under Settings > Accounts > **Windows backup** > "OneDrive
folder syncing" > Manage sync settings. If neither shows a Manage backup control, the feature is blocked
by policy (`KFMBlockOptIn`) or, on domain-joined PCs, by Microsoft for personal accounts; the script
prints both.

OneDrive's folder backup (Known Folder Move) makes Windows treat `OneDrive\Desktop`,
`OneDrive\Documents` and `OneDrive\Pictures` as your real Desktop, Documents and Pictures. NoDrive v1
does not support redirected known folders inside its sync root (native support is planned for P2),
so turn backup off **before** unlinking. Unlinking first can leave Windows pointing your Desktop at a
dead folder.

1. **Download everything.** OneDrive icon > Settings > Sync and backup > Advanced settings > Files
   On-Demand > **Download all files**. Wait until every item shows a solid green check. Stopping
   backup fails for online-only files ("Some files couldn't be moved. Download them first"), and
   adoption later is metadata-only for files that are present locally.
2. **Stop backup per folder.** Settings > Sync and backup > **Manage backup** > turn Desktop,
   Documents and Pictures off one at a time. For each, OneDrive asks:
   - *Keep the files only in OneDrive, removing them from your computer*: the cloud keeps them, your
     local Desktop/Documents/Pictures start empty, and after adoption they appear in Explorer under
     `<sync root>\Desktop` etc. as placeholders. Choose this to keep the files synced.
   - *Keep the files only on my PC, removing them from OneDrive*: files move back to
     `C:\Users\<you>\Desktop` etc. and are **deleted from the cloud** (OneDrive recycle bin). They
     are no longer synced by anything until NoDrive's folder-backup feature exists.
   Close open files first; OneNote notebooks block the move and must be handled separately.
3. **Verify the paths reverted.** In PowerShell:
   `[Environment]::GetFolderPath('Desktop')`, `('MyDocuments')`, `('MyPictures')` must print
   `C:\Users\<you>\...`, not a OneDrive path. If one still points into OneDrive, right-click that
   folder in Explorer > Properties > Location > **Restore Default**.
4. **Unlink.** Settings > Account > **Unlink this PC**. Locally available files stay in the old
   OneDrive folder as ordinary files; online-only placeholders are removed.
5. **Start NoDrive**, choose the old OneDrive folder as the sync root, and let adoption run.

## Uninstall **[planned]**

`NoDrive.Engine.exe --uninstall` disconnects the provider, offers to download everything that is
online-only (unregistering a sync root deletes unhydrated placeholders from disk; the cloud copies
are untouched), unregisters the sync root, removes autostart and the scheduled task, and deletes
`%LOCALAPPDATA%\NoDrive`. Your files stay in the sync folder as ordinary files.

## Licence

MIT. Third-party code is used only under permissive licences; see
[memory_bank/research/open-source-licences.md](memory_bank/research/open-source-licences.md).
