# Install and first run

NoDrive is a lightweight OneDrive (personal account) client for Windows 11. It shows your files in File Explorer and downloads them when you open them.

1. Get `NoDrive.Engine.exe` (the Native AOT build from the project's CI, branch `ci-binaries`, folder `engine`). Put it in a folder you keep, for example `C:\Users\<you>\NoDrive-app`.
2. Run it. A cloud icon appears in the tray. The first run opens your browser to sign in to your Microsoft account.
3. Your files appear in `C:\Users\<you>\NoDrive`. The first scan of a large OneDrive takes a few minutes; you can keep working.
4. Optional: right-click the tray icon and tick **Start NoDrive with Windows**.

Notes
- Stop OneDrive.exe for the same account before using NoDrive. They should not sync the same account at once.
- Opening a file downloads the whole file first (like OneDrive). Use **Always keep on this device** to make files available offline and **Free up space** to remove the local copy.
