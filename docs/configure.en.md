# Configure NoDrive

Open the settings page: tray icon, **Open NoDrive…** (or double-click the icon). Changes to limits and sync intervals apply immediately; the others need a restart.

Settings page
- **Language**: English, Spanish or same as Windows. Dates, times and numbers always follow your Windows regional format and time zone.
- **Bandwidth**: download and upload limits (0 = unlimited).
- **Synchronization**: how often NoDrive checks for changes, and how many downloads run in parallel.
- **Downloads**: whole file when opened (recommended) or partial reads (advanced).

Config file: `%LOCALAPPDATA%\NoDrive\config.json`. Fields: `syncRoot`, `clientId`, `language`, `downloadKiBps`, `uploadKiBps`, `pollActiveSeconds`, `pollIdleMaxSeconds`, `hydrationWorkers`, `hydrationPolicy`, `webPort`. Edit it with NoDrive closed.

Tray menu: Open NoDrive…, Pause/Resume syncing, Start with Windows, Open NoDrive folder, Quit. A **Sign in…** item appears when sign-in is needed, and **Allow N held deletions…** if a mass delete was held for your safety.

Activity tab: live transfers with progress and the recent activity (choose to show the last 25, 50, 100, 200 or 400 entries; they are kept when NoDrive restarts). The **Switch from OneDrive** tab is optional and does nothing unless you use it. The page opens only from the tray icon; if it says it is out of date, NoDrive was restarted: open it again from the tray icon.

Bulk download speed (Settings): how many files download at once when you keep a folder on this device. Gentle 2, Balanced 4 (default), Turbo 8. It applies immediately. Turbo uses most of your connection; use the download limit if you share it.
