# Install and first run

NoDrive is a lightweight OneDrive (personal account) client for Windows 11. It shows your files in File Explorer and downloads them when you open them.

1. Download `NoDrive-x64.msi` (the **NoDrive-installer-win-x64** artifact of the latest successful build on GitHub, or the `installer` folder of the `ci-binaries` branch).
2. Double-click it, accept the licence and install. No administrator rights are needed. NoDrive is installed in `%LOCALAPPDATA%\Programs\NoDrive`, appears in the Start menu and under Settings > Apps, and starts when you sign in. For a silent install: `msiexec /i NoDrive-x64.msi /qn AUTOSTART=0 SUPPRESSLAUNCH=1` (both options are optional).
3. NoDrive starts by itself. A cloud icon appears in the tray and your browser opens to sign in to your Microsoft account.
4. Your files appear in `C:\Users\<you>\NoDrive`. The first scan of a large OneDrive takes a few minutes; you can keep working. Double-click the tray icon to see what NoDrive is doing.

Notes
- NoDrive can run next to the OneDrive app on the same account (see the OneDrive guide).
- Opening an online-only file downloads the whole file first (like OneDrive). Use **Always keep on this device** to make files available offline and **Free up space** to remove the local copy.
- Windows may block unsigned programs (Smart App Control, Application Control). If the installer or NoDrive is blocked, tell the author: a signed release is planned.
- To update, run the newer MSI over the old one: NoDrive stops, the files are replaced and your sync folder and settings stay.
- To remove NoDrive: Settings > Apps > NoDrive > Uninstall. Downloaded files stay in the folder. See the troubleshooting guide if the uninstall is refused.

---
Website: <https://andresvettori.github.io/nodrive/>
