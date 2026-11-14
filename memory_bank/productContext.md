# NoDrive: product context

Why NoDrive exists, who it is for, and how it should feel. Scope and hard requirements are owned by `projectbrief.md`; Explorer integration facts with sources are in `research/explorer-ux.md`.

Created 2026-11-13.

## Why NoDrive exists

### The pain with OneDrive.exe (in the user's own terms)

- On an 8 GB Windows 11 PC, with a OneDrive of 400k+ items (about 200k today), OneDrive.exe consumed almost 4 GB of RAM.
- File Explorer hung and became unresponsive all the time.
- File move operations never finished: the dialog showed "0 files remaining", the folder had already moved, and the dialog was dead.
- These are the two classic failure modes a Cloud Files API provider can inflict on Explorer: slow or blocking work inside a cfapi callback, and keeping the whole item tree in memory. Nextcloud users reported the same 30-40 s Explorer freezes in very large VFS folders (community-reported: https://help.nextcloud.com/t/windows-11-explorer-freez-with-nextcloud-client-33-0-0-to-35-0-0-fixed-with-35-0-1-update/243345).

### The pain with rclone

- The user likes rclone's low resource use and its straightforward engineering.
- Dislikes: it supports dozens of providers (bloat for someone who only needs OneDrive), and its UX is console and admin oriented (config via CLI, `rclone mount` needs the WinFsp driver, no Explorer placeholders or status icons). rclone has no Cloud Files API support; a 2020 forum thread discussed it without any maintainer decision (community-reported, https://forum.rclone.org/t/18591). Its mount keeps directory trees in RAM (https://rclone.org/faq/ ; idle mounts of 100-500 MB are community-reported: https://forum.rclone.org/t/rclone-mount-using-lot-of-ram-memory/28760).

### What NoDrive takes from each

- From OneDrive.exe: the Explorer experience (online-only placeholders, download on open, status icons, "Always keep on this device" / "Free up space").
- From rclone: a small, single-purpose engine that uses little memory and does its job quietly.
- From neither: multi-provider abstractions, resident UI frameworks, in-memory item trees, blocking callbacks.

## Who it is for

- The developer, as the only user: a C# programmer with one personal OneDrive account of hundreds of thousands of items on a modest Windows 11 PC, who wants OneDrive's Explorer integration without OneDrive's resource use and instability.
- Secondary (if ever published under MIT): technically comfortable Windows 11 users with large personal OneDrives who are willing to unlink OneDrive.exe for the account.
- Not for: business/SharePoint tenants, multi-cloud users, people who need OneDrive extras (Personal Vault, photo backup, Office co-authoring, sharing UI).

## Problems it solves

1. **Memory**: a resident footprint that does not grow with the number of items (disk-backed index, streaming transfers), targeting under 30 MB idle with a 500k-item index.
2. **Responsiveness**: Explorer never waits on the network. Callbacks return immediately; deletes and renames are acknowledged locally and reconciled later; every pending request has a deadline and fails visibly instead of hanging.
3. **Scale**: 500k items browsed lazily; opening a folder with tens of thousands of children is answered from the local index, never from a live Graph call.
4. **Trust**: no silent overwrites, no silent deletes, no lost files on crash or uninstall. Conflicts become visible entries, not surprises.
5. **Simplicity**: one account, one folder, one tray icon, no console, no admin rights, no kernel driver, no store package.

## How it works from the user's point of view

1. **Install**: copy or run a small installer for an unpackaged, per-user app. No elevation, no driver. NoDrive refuses to start if OneDrive.exe is still linked to the same account and tells the user to unlink it first.
2. **Sign in**: the tray icon's "Sign in" opens the default browser on the Microsoft account page (authorization code + PKCE, loopback redirect). Consent is for `Files.ReadWrite` and `offline_access` only. The browser tab says "you can close this"; NoDrive keeps the refresh token protected with DPAPI. No password ever passes through NoDrive.
3. **A NoDrive folder appears in Explorer**: `%USERPROFILE%\NoDrive`, with its own node, icon and name in the navigation pane. cfapi gives this, plus the Status column and hydration verbs, on registration (https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine).
4. **Files are online-only**: every file shows the cloud icon and its real size, takes about 1 KB on disk, and folders are populated the first time they are opened. The initial enumeration of a 200k-item drive runs in the background at low priority; the user can browse folders that are already indexed while it continues, and a tray tooltip shows progress.
5. **Download on open**: double-clicking a file (or opening it from any app's Open dialog) downloads it with Explorer's own progress UI, then opens it. Large media hydrates progressively (only the ranges the app reads). The Status icon turns to the green check. "Always keep on this device" pins a file or folder; "Free up space" turns it back into a placeholder. (On recent Insider builds these verbs moved into a per-provider flyout, so the tray menu offers the same actions as a fallback: https://blogs.windows.com/windows-insider/2025/11/21/announcing-windows-11-insider-preview-build-26100-7271-dev-beta-channels/.)
6. **Upload by dropping files**: copying or saving a new file into the folder uploads it in the background; the Status icon shows syncing, then the check. Renames and deletes inside the folder complete at local speed and are propagated afterwards; remote deletes go to the OneDrive recycle bin.
7. **Remote changes show up** within about a minute while the user is active, less often when idle, without any visible activity from NoDrive.
8. **Tray menu**: status line (up to date / syncing N items / paused / offline / error), open folder, pause/resume, "free up space" and "keep on this device" for the selected or whole folder, conflicts list (opens a text/HTML view in v1), sign out, quit. Clicking a conflict opens the item's folder in Explorer.
9. **Errors**: a problem with one file (quota, invalid name, server rejected) marks that item with an error state and lists it in the tray conflicts view. Explorer shows the platform's own error dialog when a hydration fails (offline, throttled, expired download URL), always within seconds, never a frozen dialog.
10. **Uninstall / sign-out**: NoDrive warns that online-only files will be removed from disk and offers to download everything first, then unregisters the sync root cleanly (`CfUnregisterSyncRoot` permanently deletes unhydrated placeholders: https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfunregistersyncroot).

## UX principles

- **Transparent.** Files are real files in a real folder. Any app's Open/Save dialog, double-click, drag and drop and command line work. No custom file browser, no stub files like odrive's `.cloud` files (https://docs.odrive.com/docs/sync-your-odrive).
- **Quiet.** No window at startup, no console, no notifications unless something needs the user. cfapi already raises the hydration toasts; NoDrive adds none of its own in v1.
- **Never blocks.** Explorer, Office and other apps never wait on the network. If NoDrive cannot do something now, it fails fast and visibly, then retries in the background.
- **Errors visible but non-intrusive.** Per-item error states and a conflicts list in the tray; a tray icon badge for "attention needed". No modal dialogs from NoDrive itself.
- **Predictable.** Every action maps to one effect: delete locally means recycle bin remotely; rename locally means rename remotely; nothing happens to the server that the user did not do locally, and a mass delete pauses sync and asks.
- **Honest about state.** The Status column and tray status are driven from the local index and transfer state, so what Explorer shows is what NoDrive knows.
- **Lightweight.** Idle memory independent of item count; memory returned to the OS after a burst; no UI framework resident. Measured, not assumed.

## What "good" looks like vs OneDrive.exe

| Situation | OneDrive.exe (user's experience on an 8 GB PC) | NoDrive target |
|---|---|---|
| Idle with 200k-400k items | Almost 4 GB RAM | Under 30 MB Private Bytes with a 500k-item index |
| Browsing large folders | Explorer hangs, unresponsive | Folder opens from the local index; callback p99 under 5 ms |
| Moving a folder | Dialog stuck at "0 files remaining", folder already moved | Dialog completes at local-move speed; server catches up asynchronously |
| Offline or throttled | Hangs and stuck dialogs | Operations complete or fail with a visible error within 10 s |
| After downloading 10 GB | Memory stays high | Back to within 10 MB of idle within 60 s |
| Crash or kill mid-transfer | Unclear state | Journal replay on restart, no stuck placeholders, Explorer needs no restart |
| Installation | Bundled with Windows, machine-wide | Unpackaged, per-user, no admin, no driver |
| Scope | Everything (Vault, photos, Office, sharing) | Browse, download, upload new files; later editing |

## What NoDrive deliberately does not do

- Support any provider other than OneDrive personal.
- Run alongside OneDrive.exe for the same account.
- Offer "Shared with me" or "Recent" views (deprecated Graph endpoints stop returning data after November 2026: https://learn.microsoft.com/en-us/graph/api/drive-sharedwithme?view=graph-rest-1.0).
- Provide a console or admin-style interface; configuration is a tray menu and a JSON file.
- Keep a UI framework resident; any settings window is a separate on-demand process (later phase).
- Add entries to the Windows 11 modern context menu (requires package identity and a native in-proc DLL: https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/integrate-packaged-app-with-file-explorer).
- Edit existing files in place in v1 (P2), show thumbnails for online-only photos in v1 (P2), or push notifications via socket.io in v1 (P2).
- Sync Personal Vault, OneNote packages or items with Windows-invalid names; these are shown as unsupported.
- Work on Windows 10, macOS or Linux.

## References

- `research/explorer-ux.md`: what cfapi gives for free, what needs package identity, Insider-build changes to the hydration verbs, prior art (Nextcloud, Mountain Duck "Integrated" mode on cfapi: https://blog.cyberduck.io/2025/08/12/integrated-connect-mode/), toast options.
- `projectbrief.md`: scope, non-goals, hard requirements, confirmed decisions.
- `projectPlan.md`: phases and acceptance tests that make the "good" column measurable.
