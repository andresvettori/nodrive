# OneDrive.exe: unlink behaviour, detection surface, re-provisioning

Research pass 2026-11-13 (one agent, web sources). Evidence labels: **[MS]** Microsoft Learn / support.microsoft.com, **[MS-QA]** Microsoft Q&A (community or advisor answers, not official docs), **[community]** third party. Feeds `decisions/ADR-0008.md`.

## 1. What "Unlink this PC" does with Files On-Demand

- [MS] "Files marked as Available on this device will remain in your computer's OneDrive folders. Files marked as Available when online will only be accessible from OneDrive on the web." https://support.microsoft.com/en-us/office/how-to-remove-an-account-in-onedrive-72699268-9e64-45bd-b723-9a19f4512fd1
- [MS] Re-linking "won't download all your files (just their placeholders to save space)". https://support.microsoft.com/en-us/office/unlink-and-re-link-onedrive-3c4680bf-cc36-4204-9ca5-e7b24cdd23ea
- [MS-QA] Client dialog text: "Locally available files will remain on this device while online only files will be removed." https://learn.microsoft.com/en-us/answers/questions/5083385/your-onedrive-files-will-stop-syncing-locally-avai
- [MS] `CfUnregisterSyncRoot`: hydrated placeholders revert to normal files; unhydrated placeholders and not-fully-populated directories are permanently deleted; fails with `ERROR_CLOUD_FILE_INVALID_REQUEST` while a provider is connected. https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfunregistersyncroot
- Conclusion: after unlink the folder holds only ordinary files. Which API OneDrive.exe calls is not documented; the behaviour matches `CfUnregisterSyncRoot` (inference).

## 2. Identifying a linked personal account and its folder

| Signal | Where | Reliability | Notes |
|---|---|---|---|
| Account linked | `HKCU\Software\Microsoft\OneDrive\Accounts\Personal` (`UserFolder`, `UserEmail`, `UserName`) | community-verified (Metasploit enum_onedrive, TechCommunity, C# Corner); no Microsoft doc | Business accounts are `Business1..n` with `Business=1`, `ServiceEndpointUri`, `SPOResourceId`. A `cid` value is probable but unverified; tolerate absence. |
| Sync root registered | `HKLM\Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\OneDrive!<SID>!Personal`, `UserSyncRoots\<SID>` = path | [MS] layout and id format documented; exact consumer suffix community-observed | World-readable HKLM. Disappears on unlink. https://learn.microsoft.com/en-us/windows/win32/shell/integrate-cloud-storage |
| Folder is some provider's root | `CfGetSyncRootInfoByPath` (fails if not under a sync root; `CF_SYNC_ROOT_INFO_PROVIDER` gives `ProviderName`, `ProviderStatus`) | [MS] | Returns filter-level info, not the shell id string. https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfgetsyncrootinfobypath |
| WinRT alternative | `StorageProviderSyncRootManager.GetCurrentSyncRoots()` / `GetSyncRootInformationForFolder` | [MS] | https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.storageprovidersyncrootmanager |
| Env vars | `OneDrive`, `OneDriveConsumer`, `OneDriveCommercial` | community; undocumented; can be stale | Hints only. |
| Process | `OneDrive.exe` from `%LOCALAPPDATA%\Microsoft\OneDrive\` (per-user) or `%ProgramFiles%\Microsoft OneDrive\` (per-machine) | [MS] install locations https://learn.microsoft.com/en-us/sharepoint/per-machine-installation | Autostart `HKCU\...\Run\OneDrive` = `"<path>\OneDrive.exe" /background` (community). OneDrive's own status APIs refuse elevated callers; NoDrive runs unelevated anyway. |

## 3. Re-provisioning after unlink or uninstall

- New users: Default profile hive `Run\OneDriveSetup` = `%SystemRoot%\System32\OneDriveSetup.exe /thfirstsetup` installs OneDrive at first sign-in (community-reported; no Microsoft doc). https://github.com/Raphire/Win11Debloat/issues/366
- Updates can bring the app back; Microsoft moderator recommends the policy "Prevent the usage of OneDrive for file storage" (`HKLM\Software\Policies\Microsoft\Windows\OneDrive`, `DisableFileSyncNGSC`=1). [MS] policy CSP lists Pro/Enterprise/Education only: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-system#disableonedrivefilesync ; Home support community-reported.
- Uninstall: Settings > Apps, or `OneDriveSetup.exe /uninstall`, or `winget uninstall Microsoft.OneDrive` [MS-QA].

## 4. Implications for NoDrive (see ADR-0008)

- Detection = registry read + `SyncRootManager` scan + `CfGetSyncRootInfoByPath` on the chosen root; all unelevated, read-only, microseconds.
- Adoption reconciles ordinary files only (no foreign placeholders survive unlink), so it is `CfConvertToPlaceholder` + `CfSetInSyncState` on size/mtime match, hash verified lazily.
- `--status` should surface OneDrive presence and the policy value so the user can keep it from coming back.

## Open questions

- Exact consumer sync-root id suffix (`!Personal` vs `!Personal|<driveId>`): check on the test PC.
- Whether `Accounts\Personal` is removed immediately on unlink or only on sign-out/uninstall: check on the test PC.
- Cost of `CfConvertToPlaceholder` over 200k files on 24H2/25H2 (P0 step 4/6 extension).

## Status of the implementation (2026-11-16)

Decision changed from "refuse to start" to coexistence (ADR-0013). Read-only detection uses the process list, `HKCU\Software\Microsoft\OneDrive\Accounts\Personal` (`UserEmail`, `UserFolder`) and whether Desktop/Documents/Pictures live under `UserFolder`. Measured on the user's PC: Folder Backup was on for all three; OneDrive was installed and linked but not always running; the read-only scan of the OneDrive folder (about 85,000 items, mostly online-only) took seconds and found 76 identical files, 0 different, 11 only on the PC. The take-over step (unlink first, then adopt) is built but has not been run on a real OneDrive folder.
