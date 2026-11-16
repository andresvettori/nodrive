# Configure NoDrive

Open the settings page: tray icon, **Open NoDrive…** (or double-click the icon). Changes to limits and sync intervals apply immediately; the others need a restart.

Settings page
- **Language**: English, Spanish or same as Windows. Dates, times and numbers always follow your Windows regional format and time zone.
- **Bandwidth**: download and upload limits (0 = unlimited).
- **Synchronization**: how often NoDrive checks for changes, and how many downloads run in parallel.
- **Downloads**: whole file when opened (recommended) or partial reads (advanced).

Config file: `%LOCALAPPDATA%\NoDrive\config.json`. Fields: `syncRoot`, `clientId`, `language`, `downloadKiBps`, `uploadKiBps`, `pollActiveSeconds`, `pollIdleMaxSeconds`, `hydrationWorkers`, `hydrationPolicy`, `webPort`. Edit it with NoDrive closed.

Tray menu: Open NoDrive…, Pause/Resume syncing, Start with Windows, Open NoDrive folder, Quit. A **Sign in…** item appears when sign-in is needed, and **Allow N held deletions…** if a mass delete was held for your safety.
