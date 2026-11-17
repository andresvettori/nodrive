# Troubleshooting

- **Logs**: `%LOCALAPPDATA%\NoDrive\logs`. The Activity page shows recent operations and problems.
- **Status from a terminal**: `NoDrive.Engine.exe --status` prints registration, shell flags and index state.
- **Sign-in required**: use the button on the Activity page or the tray item **Sign in…**.
- **A file opens as corrupted in an app**: make sure the download mode is **Whole file when opened** (Settings > Downloads), then restart NoDrive.
- **Menus or icons missing in Explorer**: restart Explorer or sign out and in again.
- **Deletions on hold**: NoDrive holds a burst of more than 200 deletions in 10 minutes. Review, then press **Allow them**. Cloud deletes go to the OneDrive recycle bin.
- **Remove NoDrive**: quit it, run `NoDrive.Engine.exe --cleanup`. Files you never opened are removed by Windows when the sync root is unregistered; open or keep the files you need first.
- **The uninstall stops with an error**: NoDrive found uploads, deletes or renames that have not reached OneDrive yet. Start NoDrive, wait until the Activity page shows nothing waiting, then uninstall again (`NoDrive.Engine.exe --pending` shows the count).
- **Windows blocks NoDrive or the installer**: unsigned programs can be stopped by Smart App Control or Application Control (error 0x800711C7 for freshly built files). Use the official MSI; a signed release is planned.
- **The settings page says it is out of date**: NoDrive restarted; open the page again from the tray icon.
