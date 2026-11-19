# NoDrive

A deliberately lightweight OneDrive client for Windows 11.

- One provider (personal OneDrive), one platform (Windows), one job: make your OneDrive files appear
  in File Explorer as on-demand placeholders, download on open, upload on drop.
- Built on the Windows Cloud Files API (the same platform OneDrive uses), so Explorer shows the
  status icons, the Status column, hydration progress and the "Always keep on this device / Free up
  space" verbs natively.
- Written in C# on .NET 11, published as a Native AOT, headless process with a tray icon. Idle memory
  is about 15 to 30 MB and does not grow with the number of files (measured with 90,000 items). It is
  designed never to block Explorer.

**Status (2026-11-16): `0.9.0-beta`, in daily use on the author's PC; friends-and-family distribution is being prepared.** Browsing, on-demand download, keep and
free up space, remote changes mirrored into folders you have opened, upload of new files and folders,
delete and rename from Explorer, sign-in recovery, tray menu, a local settings and activity page
(English and Spanish), bandwidth limits, a per-user MSI installer, and an optional "Switch from
OneDrive" assistant are implemented and verified by the author. Edits to files you already downloaded are uploaded (with a conflict copy when the cloud changed first), NoDrive
catches up after being stopped, and a "Report a problem" button creates one file to send for support. Not done yet:
cloud thumbnails for online-only pictures and code signing. See [memory_bank/progress.md](memory_bank/progress.md) for the full status and
[memory_bank/projectPlan.md](memory_bank/projectPlan.md) for the plan.

## Install

1. Download `NoDrive-x64.msi` from the **NoDrive-installer-win-x64** artifact of the latest successful run of the
   `windows` workflow on GitHub (Actions tab; sign-in required while the repository is private). The same file is
   on the `ci-binaries` branch under `installer/`.
2. Double-click it. No administrator rights are needed: it installs to
   `%LOCALAPPDATA%\Programs\NoDrive`, adds a Start menu entry and starts NoDrive when you sign in
   (pass `AUTOSTART=0` to skip that, `SUPPRESSLAUNCH=1` to not start it right after installing).
3. On the first run your browser opens to sign in to your Microsoft account. Your files appear in
   `%USERPROFILE%\NoDrive`. The first scan of a large OneDrive takes a few minutes.

Unsigned builds may be blocked by Windows Application Control or Smart App Control; the project does not
have a code-signing certificate yet. Details and guides (English and Spanish) are in [docs/](docs/):
[install](docs/install.en.md), [configure](docs/configure.en.md),
[OneDrive coexistence](docs/onedrive-coexistence.en.md),
[own Entra app ID](docs/custom-app-registration.en.md), [troubleshooting](docs/troubleshooting.en.md)
(`*.es.md` for Spanish).

NoDrive and the OneDrive app can run side by side on the same account (see below).

## Using NoDrive

- **Explorer.** Open folders and files as usual. Opening an online-only file downloads the whole file first
  (like OneDrive; Windows shows its own "Downloading" dialog), which is what makes apps such as Photos work.
  Right-click for **Always keep on this device** and **Free up space**. Deleting, renaming and moving files
  and folders in Explorer is sent to the cloud (deletes go to the OneDrive recycle bin); new files and
  folders you add are uploaded.
- **Tray icon.** Double-click (or **Open NoDrive...**) opens the local settings and activity page.
  The menu also has **Pause / Resume syncing**, **Start NoDrive with Windows**, **Open NoDrive folder**,
  **Sign in...** (when needed), **Allow N held deletions...** (see Safety) and **Quit**.
- **Settings and activity page** (`http://localhost:47653`, served by the engine, only from this PC):
  live transfers with progress, recent activity (last 25 to 400 entries, kept across restarts), download and
  upload speed limits, sync intervals, language (English, Spanish or same as Windows), start with Windows,
  where everything lives, OneDrive status, the optional **Switch from OneDrive** tab, and the guides.
  Dates, times and numbers follow your Windows regional format and time zone.

### Safety rules

No file is overwritten or removed without a way back: uploads use "fail if the name exists" and fall back to a
`(NoDrive conflict ...)` copy; remote deletes carry an `If-Match` check and go to the recycle bin; more than 200
deletions in 10 minutes are held until you allow them from the tray; a file is never dehydrated while it is not
in sync; every pending upload, delete and rename is journalled in SQLite before it is sent and replayed after a
crash; a damaged index is set aside and rebuilt from the cloud.

## Configure

State lives under `%LOCALAPPDATA%\NoDrive\`: `config.json`, `index.db` (SQLite via the system
`winsqlite3.dll`), `token.bin` (refresh token, DPAPI-encrypted), `activity.jsonl`, `logs\`. Most settings can be
changed on the settings page; `config.json` (edit it while NoDrive is stopped) has:

```jsonc
{
  "clientId": "6711e7c6-ec09-42df-a5bb-bfb449119fdc",  // default sign-in app (see below)
  "syncRoot": null,                // null = %USERPROFILE%\NoDrive; NTFS, not a drive root, not inside another sync root
  "hydrationPolicy": "full",       // "full" (whole file on open, recommended) or "progressive"; applied at start
  "hydrationWorkers": 4,           // parallel downloads; applied at start
  "downloadKiBps": 0,              // limits in KiB/s, 0 = unlimited, applied immediately
  "uploadKiBps": 0,
  "pollActiveSeconds": 60,         // how often to look for cloud changes while there is activity
  "pollIdleMaxSeconds": 900,       // upper bound when idle
  "populationBudget": 150000,      // placeholders created in the background for very large folders
  "language": "auto",              // "auto", "en" or "es"
  "webPort": 0                     // 0 = 47653, or a free port if that one is taken
}
```

### Entra app registration: who does it and when

NoDrive signs in with OAuth 2.0 authorization code + PKCE through your default browser. It is a
**public client**: an app registration gives it a client ID and no secret, and that client ID is
meant to be embedded in the shipped binary.

| You are | What you do |
|---|---|
| **Using a release build of NoDrive** | Nothing. The build embeds NoDrive's client ID. Sign in, consent to `Files.ReadWrite` and `offline_access`, done. Optionally set your own `clientId` to get your own throttling budget. |
| **Building from source for yourself** | Register an app once (steps below) in your own tenant and put its client ID in `config.json`. |
| **Publishing NoDrive to others** | Your registration becomes the embedded default. Add branding and consider Microsoft publisher verification so the consent screen does not say "unverified". |

Microsoft Graph throttles per application as well as per user, so every installation sharing one embedded ID draws from
one app-level budget; that is why `clientId` stays overridable. Because there is no client secret, any program could present
the client ID to ask for consent; this is accepted practice for desktop public clients, PKCE protects the code in flight and
the consent screen always shows the permissions.

Steps to register (one time):

1. A personal Microsoft account cannot create app registrations by itself. Create a free Entra tenant and open
   **Microsoft Entra admin center > App registrations > New registration**.
2. Name `NoDrive`; supported account types **Accounts in any organizational directory and personal Microsoft accounts**.
3. Platform **Mobile and desktop applications**, redirect URI `http://localhost`; under Authentication set
   **Allow public client flows: Yes**.
4. API permissions (delegated, Microsoft Graph): `Files.ReadWrite`, `offline_access`. Nothing else.
5. Copy the **Application (client) ID** into `clientId`. There is no client secret.

## Coexistence with the OneDrive app

NoDrive and OneDrive can run on the same PC and account, like two computers: a change in one reaches the other through
the cloud. They use different folders; NoDrive refuses to register a sync root inside a folder another provider owns.
The settings page shows whether OneDrive is running, installed but stopped, or absent, and which of Desktop, Documents
and Pictures OneDrive backs up (Folder Backup). NoDrive never changes OneDrive's settings.

**Switch from OneDrive (optional).** The settings page has a tab that guides you if you want NoDrive to become your only
client: (1) where you are now, (2) a read-only comparison of your OneDrive folder with the cloud (identical, online-only,
different, only on this PC), (3) the steps only OneDrive can do, which you do yourself (stop Folder Backup keeping the files
in place, unlink the PC), (4) take over the folder: NoDrive restarts on it and links every file with the same name, size and
modified time to its cloud copy instead of uploading it again. Nothing in this flow deletes or overwrites a file, and it never
runs unless you open it. The manual steps, including Folder Backup, are in [docs/onedrive-coexistence.en.md](docs/onedrive-coexistence.en.md).

## Uninstall

Settings > Apps > NoDrive > Uninstall. The installer quits NoDrive, checks that no upload, delete or rename is still waiting
(it stops with an error if there is), unregisters the sync root and removes the autostart entry. Files you had downloaded stay
in the folder as ordinary files; online-only placeholders are removed by Windows (the cloud copies are untouched). If you install
again later, NoDrive links those files back to their cloud copies instead of creating duplicates.

Command line (the installer uses these): `NoDrive.Engine.exe --version`, `--doctor` (health checks), `--report` (support zip), `--quit`, `--pending` (exit code 3 when work is queued),
`--uninstall [--force] [--delete-data] [--delete-root]`, `--status`, `--cleanup`, and the options `--sync-root <path>` and
`--data-dir <path>`.

## Build and test

Prerequisites: .NET SDK 11.0.100 or later (`global.json`) on Windows, macOS or Linux. To run or publish the engine you need
Windows 11 x64 (24H2 with the August 2025 update or later) and the Visual Studio "Desktop development with C++" workload (Native
AOT uses the MSVC linker). Windows-targeted projects restore and build on macOS/Linux thanks to `EnableWindowsTargeting`, but only
run on Windows.

```powershell
dotnet build                 # whole solution; 0 warnings expected (warnings are errors)
dotnet test                  # cross-platform unit tests (Microsoft.Testing.Platform, xunit.v3)
dotnet publish src/NoDrive.Engine -c Release -r win-x64 -p:PublishAot=true -o artifacts/engine-win-x64   # Windows only
```

| Project | Role |
|---|---|
| `src/NoDrive.Core` | Index (SQLite), delta ingestion, population and hydration policy, read-ahead planner, bandwidth limiter. Platform neutral. |
| `src/NoDrive.Graph` | Hand-written Microsoft Graph client: PKCE sign-in, delta, download, upload (simple and sessions), delete, move. |
| `src/NoDrive.Engine` | The always-on Windows process: tray, Cloud Files provider, upload/ops/applier services, local web page, lifecycle commands. |
| `tests/NoDrive.Core.Tests` | Unit and property tests with a fake Graph server (48 tests). |
| `installer/` | WiX v5 per-user MSI (`NoDrive.wxs`). |
| `docs/` | User guides in English and Spanish, linked from the settings page. |
| `spikes/`, `tools/` | P0 measurement apps and scripts; icon generator. |
| `memory_bank/` | Project memory: brief, architecture, plan, progress, ADRs, research. Start with [projectbrief.md](memory_bank/projectbrief.md). |

The `windows` GitHub Actions workflow builds, tests, publishes the Native AOT engine and the spikes, builds the MSI (WiX 5.0.2,
pinned to stay on the MS-RL licence), uploads them as artifacts and force-pushes the binaries to the `ci-binaries` branch
(`engine/`, `installer/`, `BUILD.txt` with the commit). Build the MSI locally on Windows with:

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
wix build installer/NoDrive.wxs -ext WixToolset.UI.wixext -arch x64 -d EnginePath=artifacts/engine-win-x64 -d Version=0.1.0 -o NoDrive-x64.msi
```

Developer note: a freshly built, unsigned `NoDrive.Engine.dll` (the JIT build) can be blocked by Windows Application Control
(error 0x800711C7). Test with the Native AOT single-file exe from CI instead.

## Versions and support

Versions look like `0.9.0-beta.142` (the number after `beta` is the build). The version is shown at the bottom of the NoDrive page, in the tray tooltip, in the installer's file name and in the log header. Release history: [CHANGELOG.md](CHANGELOG.md) (user summary in [docs/whats-new.en.md](docs/whats-new.en.md)). How versions, tags and releases work: [memory_bank/release-process.md](memory_bank/release-process.md); how builds are tested: [memory_bank/qa-plan.md](memory_bank/qa-plan.md).

If something goes wrong: tray icon > **Report a problem...** creates one zip with logs and a health report to send to whoever supports you ([guide](docs/report-a-problem.en.md)).

## Contributing and security

Contributions are welcome: see [CONTRIBUTING.md](CONTRIBUTING.md) (build, tests, the rules that are never traded for a feature). To report a vulnerability privately, see [SECURITY.md](SECURITY.md).

## Licence

MIT. Third-party code is used only under permissive licences; see
[memory_bank/research/open-source-licences.md](memory_bank/research/open-source-licences.md).
