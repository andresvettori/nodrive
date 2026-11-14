# Known Folder Move ("Back up folders"): turning it off, detecting it, supporting it later

Research pass 2026-11-13 (one agent, web sources). Labels: **[MS]** Microsoft Learn / support.microsoft.com, **[Vendor]** Dropbox/Google/FileCloud docs, **[community]** forums, blogs, Q&A answers. The user has folder backup enabled for Desktop, Documents and Pictures on a personal account, so this is a required migration step (ADR-0008, E1b) and a future feature (P2).

## 1. What folder backup is

OneDrive's "Back up folders" (Known Folder Move, KFM) redirects the Windows known folders Desktop, Documents and Pictures (plus Screenshots and Camera Roll under Pictures) into the OneDrive folder: the shell's registered path for the Desktop becomes `%USERPROFILE%\OneDrive\Desktop`, and every app asking Windows for the Desktop gets that path. Music, Videos and Downloads are not handled on Windows. [MS] https://learn.microsoft.com/en-us/sharepoint/redirect-known-folders , https://support.microsoft.com/en-us/office/back-up-your-folders-with-onedrive-d61a7930-a6fb-4b95-b28a-6552e77c3057

## 1b. When the option is not visible

The user reported not seeing "Back up folders" / "Manage backup" in their OneDrive settings (2026-11-13). The PC is the user's personal notebook (not company-managed), so the domain-join block below is unlikely for them. Possible causes, most likely first:
- The control moved: current client = cloud icon > gear > Settings > **Sync and backup** tab > **Manage backup** button [MS] https://support.microsoft.com/en-us/office/back-up-your-folders-with-onedrive-d61a7930-a6fb-4b95-b28a-6552e77c3057 ; Windows 11 Settings > Accounts > Windows backup > "OneDrive folder syncing" also leads there. Older builds had a "Backup" tab.
- Backup was never turned on (the toggles exist but are off); then no known folder is redirected and nothing needs migrating.
- Policy `KFMBlockOptIn` (HKLM\SOFTWARE\Policies\Microsoft\OneDrive) hides the option (unlikely on a personal PC).
- [MS] On domain-joined PCs Microsoft blocks folder backup to personal accounts ("Moving known folders to personal OneDrive accounts is already blocked on domain-joined PCs", https://learn.microsoft.com/en-us/sharepoint/use-group-policy). Not applicable to a personal notebook.
Diagnostic: `spikes/onedrive-state.ps1` prints domain/Entra join state, the account keys, `KfmFoldersProtectedNow`, the three known-folder paths versus their defaults, and the policy values. **If no known folder is redirected, folder backup is off and the migration section does not apply.**

### 1c. The user's actual state (measured 2026-11-14 with `onedrive-state.ps1`)

Folder backup **is on**: `KfmFoldersProtectedNow=3584` (Desktop+Documents+Pictures) and `SHGetKnownFolderPath` resolves Desktop -> `C:\Users\<PC_USER>\OneDrive\Desktop`, Documents -> `...\OneDrive\Documentos`, Pictures -> `...\OneDrive\Fotos`. The user also found the setting in the client (it exists; the earlier "not visible" was a UI-location issue). Design notes: the folder names under the OneDrive root are **localised** (Spanish `Documentos`, `Fotos`), so adoption and the future KFM feature must map known folders by path, never by English names; the user reports "some others" also backed up (likely Screenshots / Camera Roll under Pictures). The PC is not domain/Entra joined and no KFM policies are set. The README migration procedure therefore applies before adoption (P1).

## 2. Turning it off in the current client (2025-2026 UI)

- Path [MS]: OneDrive icon > Settings > **Sync and backup** > **Manage backup** > turn the folder's toggle off.
- The client then asks, per folder (exact labels) [MS]: **"Keep the files only in OneDrive, removing them from your computer"** or **"Keep the files only on my PC, removing them from OneDrive"**. Older builds (and the macOS section of the article) instead left the files in OneDrive and dropped a **"Where are my files"** shortcut into the now-empty local folder. [community] Northwestern IT KB confirms the shortcut behaviour on Windows: https://services.northwestern.edu/TDClient/30/Portal/KB/PrintArticle?ID=1310
- Documented failure modes [MS]: *"Some files couldn't be moved. Download them first by changing their state to Always keep on this device"*; *"Any open files cannot be moved"*; OneNote notebooks block the move.
- Microsoft does not state explicitly for Windows that the known-folder paths revert; the "only on my PC" option implies it, and community KBs observe it. Verify on the target machine with `SHGetKnownFolderPath`.
- **Order matters**: [community] several threads report that after *Unlink this PC* without turning backup off, `User Shell Folders` still points into `...\OneDrive\...` and had to be fixed by hand (Explorer > folder Properties > Location > Restore Default). https://learn.microsoft.com/en-us/answers/a/8201066 . Also, moving files out of the OneDrive folder is a cloud delete (recycle bin) [MS, generic].
- Admin-side equivalent [MS]: policy `KFMBlockOptIn = 2` "redirect the known folders back to the user's device" (files remain in OneDrive). https://learn.microsoft.com/en-us/sharepoint/use-group-policy

## 3. Registry and API mechanics

- Operative key: `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders` (REG_EXPAND_SZ): `Desktop`, `Personal` (Documents), `My Pictures`, plus GUID-named values `{754AC886-...}` ThisPCDesktopFolder, `{F42EE2D3-...}` LocalDocuments, `{0DDD015D-...}` LocalPictures. Community reports both the named and GUID values must agree. `...\Shell Folders` is a legacy cache: never read or write it [MS, Raymond Chen] https://devblogs.microsoft.com/oldnewthing/20110322-00/?p=11163
- Canonical IDs [MS] https://learn.microsoft.com/en-us/windows/win32/shell/knownfolderid : FOLDERID_Desktop `{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}`, FOLDERID_Documents `{FDD39AD0-238F-46AF-ADB4-6C85480369C7}`, FOLDERID_Pictures `{33E28130-4E1E-4676-835A-98395C3BC3BB}`, FOLDERID_Screenshots, FOLDERID_CameraRoll, FOLDERID_PublicDesktop (common, never moved by KFM).
- OneDrive side [community, undocumented]: `HKCU\Software\Microsoft\OneDrive\Accounts\Personal\KfmFoldersProtectedNow` bit map Desktop 0x200, Documents 0x400, Pictures 0x800 (https://stealthpuppy.com/onedrive-knownfoldermove-loganalytics/). Diagnostic only.
- **Reliable detection** [MS]: compare `SHGetKnownFolderPath(id, 0)` with `SHGetKnownFolderPath(id, KF_FLAG_DEFAULT_PATH | KF_FLAG_NOT_PARENT_RELATIVE)` ("where it would have been if never customised", https://devblogs.microsoft.com/oldnewthing/20121219-00/?p=5793); if they differ, the folder is redirected; then test whether the current path lies under OneDrive's `UserFolder` or any `SyncRootManager\<id>\UserSyncRoots\<SID>` path, including NoDrive's own root. `IKnownFolderManager::Redirect(..., KF_REDIRECT_CHECK_ONLY)` returns S_OK if already redirected. `IKnownFolder::GetRedirectionCapabilities` exposes policy denial (`DisablePersonalDirChange`). https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/ne-shobjidl_core-_kf_redirection_capabilities
- **Redirect API** [MS]: `SHSetKnownFolderPath(rfid, KF_FLAG_DONT_UNEXPAND, NULL, path)` only changes the registration (user privileges suffice for per-user folders; it does not move files). `IKnownFolderManager::Redirect` is the Explorer "Location tab" engine: `KF_REDIRECT_COPY_CONTENTS | KF_REDIRECT_DEL_SOURCE_CONTENTS | KF_REDIRECT_WITH_UI`, exclusions for known subfolders, error string out. https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shsetknownfolderpath , https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iknownfoldermanager-redirect . Refresh via `SHChangeNotify(SHCNE_RENAMEFOLDER ...)`; community consensus is that the live Desktop view needs an Explorer restart or logoff.
- Microsoft's note to developers [MS]: "To redirect the Documents, Pictures, or Desktop folders to OneDrive, use OneDrive Known Folder Move instead" (https://learn.microsoft.com/en-us/windows/win32/shell/working-with-known-folders). Nothing documents known folders inside a non-OneDrive cfapi root.

## 4. KFM limitations Microsoft documents (apply to a future NoDrive implementation too)

Path < 260 chars; no files > 250 GB; invalid characters and reserved names; the selected folder must not contain another important folder or the OneDrive folder itself; reparse points (junctions, symlinks) are refused; open files block the move; OneNote notebooks must be reopened; `desktop.ini` is not synced; `.pst` files migrate and Outlook may complain; Public Desktop is separate. [MS] restrictions: https://support.microsoft.com/en-US/onedrive/restrictions-and-limitations-in-onedrive-and-sharepoint

## 5. Third-party precedent

- **Dropbox** became a cfapi provider in 2024 [Vendor] https://help.dropbox.com/installs/dropbox-for-windows . Its 2020 "computer backup" redirected Desktop/Documents/Downloads into the Dropbox folder (community: Dropbox forum staff replies; orphaned `User Shell Folders` after uninstall). Its current Dropbox Backup product copies files instead and explicitly does not redirect [Vendor] https://help.dropbox.com/organize/dropbox-backup-faq . Nothing says Dropbox ever allowed online-only known folders.
- **Google Drive for desktop** mirrors Desktop/Documents (no redirection, no streaming) [Vendor] https://support.google.com/drive/answer/13401938
- **FileCloud Desktop** actively resets known-folder redirection into its sync root at logout [Vendor] https://docs.filecloud.com/fcdoc/latest/server/filecloud-client-applications-and-add-ins/filecloud-desktop-for-windows/known-folder-redirection-and-filecloud-desktop
- **Nextcloud** treats redirected folders as read-only (community). Conclusion: no vendor other than Microsoft ships online-only known folders; the mechanism (redirect + cfapi root) is possible but its shell interactions (Desktop view, wallpaper, Start menu, indexer, Storage Sense dehydration, `.lnk` handling) are unverified territory.

## 6. Implications for NoDrive

- **Migration procedure** (README, ADR-0008): download everything first, turn backup off per folder choosing deliberately, verify paths reverted, then unlink.
- **Detection (E1b)**: known-folder check via `SHGetKnownFolderPath` default-path comparison for Desktop, Documents, Pictures, Screenshots, Camera Roll; if any lies under the chosen sync root, adoption stops with a tray message explaining the fix.
- **P2 feature "Folder backup"**: opt-in per folder; `IKnownFolderManager::Redirect` with copy + delete-source + UI into `<root>\Desktop` etc.; pin the folder (always keep on this device) by default so Desktop items are never online-only; refuse if policy denies redirection or if the folder holds reparse points/open files; `SHChangeNotify` then prompt for Explorer restart; reversible via Redirect back to the default path with copy. Verification matrix before release: Desktop view and icon layout, wallpaper, Start/taskbar shortcuts, Windows Search, Storage Sense, Office save-as into Documents, screenshots to Pictures\Screenshots.

## Open questions

- Confirm on the target build which stop-backup dialog appears and that `User Shell Folders` reverts to `%USERPROFILE%\...` for all three folders and the GUID values.
- Whether Storage Sense would dehydrate a pinned Desktop folder (it should not; pinned excluded) and how Explorer's Desktop view treats placeholders.
- Whether `.lnk` shortcuts on the Desktop survive as placeholders (community reports duplicate shortcuts across machines with OneDrive KFM).
