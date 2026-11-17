# Changelog

All notable changes to NoDrive, newest first. Format: [Keep a Changelog](https://keepachangelog.com/). Versioning: see [memory_bank/release-process.md](memory_bank/release-process.md).
A build is named `<version>-<channel>.<build>` (for example `0.9.0-beta.142`); the notes below are per `<version>-<channel>`, and every build of it shares them. The page footer, the tray tooltip, `NoDrive.Engine.exe --version`, the installer file name and the log header all show the exact build.
Versions before `0.9.0-beta` were development milestones, not numbered builds; they are listed retroactively.

## [Unreleased]

## [0.9.0-beta] - 2026-11-16

First beta: feature complete for personal use on Windows 11, hardened for reliability.

### Added
- **Edits to files already in the cloud** are uploaded: change detection (live and after a restart), `If-Match` version check, a conflict copy when the cloud changed first (your version is never lost, the original shows the cloud version), protection of locally edited files against cloud deletes and updates, and merging of "safe-save" patterns (delete and re-create) into one update.
- **Catch-up after being stopped** (crash, update, reboot): NoDrive compares the disk with its index and replays edits, new files, renames, moves and deletes made meanwhile. Items are judged deleted only if NoDrive knows they existed on disk, and if an unusual number is missing (a wiped folder, a restored backup) nothing is deleted in the cloud: the page offers "Bring them back" or "Delete them in the cloud too".
- **Sleep, resume and network changes**: wake-up and network-available events trigger an immediate check, clear stale back-off and rate limits, and re-check the disk.
- **Self-protection**: a watchdog fails requests stuck for 50 s so Explorer never waits; memory is released above 400 MB and the engine restarts itself if it stays above 1.2 GB; a database integrity check at start (a damaged index is set aside and rebuilt); downloads are refused with a clear message when the disk is nearly full; cloud names Windows cannot hold (invalid characters, trailing dots, reserved names, case collisions) are skipped and listed instead of breaking a folder.
- **Support tools**: "Report a problem" (tray menu and Help tab) creates one zip on the Desktop with logs, recent activity, a health report and Windows' cloud-files log, with an option to leave file names out; `--doctor`, `--report`, `--version` commands; a log header (version, build, Windows, language, support ID), 14-day / 60 MB log retention and crash notes; the Support ID is shown in the page footer.
- **Versioning**: one version source, shown in the page footer, tray tooltip, log header, exe properties, installer file name and Apps list; GitHub releases are created from `v*` tags with the MSI and its SHA-256.
- Guides: report a problem, what's new (English and Spanish).

### Changed
- A delete made in Explorer now waits 10 s before it is sent, so an application's "save" does not become a delete plus an upload.

## [0.5.0] - 2026-11-16 (milestone)
- Optional **Switch from OneDrive** assistant (read-only scan, guided steps, take-over of the OneDrive folder).
- Adoption of identical files by name, size and modified time (no duplicates after a reinstall); repair walk; linking of folders that are open in Explorer; population of folders that already hold local files; empty-folder population fix.
- **WiX v5 per-user MSI** installer with upgrade and uninstall that protects unsynced work; `--quit`, `--pending`, `--uninstall`; per-user single instance; crash restart (max 3 in 10 minutes); index rebuild on corruption.

## [0.4.0] - 2026-11-15 (milestone)
- **Local settings and activity page** in English and Spanish (Windows regional formats), live transfers, persistent activity, bandwidth limits, tray menu (open, pause, autostart), app icon, OneDrive status card, guides.

## [0.3.0] - 2026-11-15 (milestone)
- **Full hydration policy** (whole file on open, like OneDrive): fixes Photos reporting downloaded pictures as corrupted.
- **Upload** of new files and folders (simple and resumable), conflict copies, **delete and rename** from Explorer with a mass-delete guard, remote changes mirrored into folders on disk.

## [0.2.0] - 2026-11-14 (milestone)
- First mount of the real OneDrive: PKCE sign-in with recovery, delta index in SQLite, lazy folders with background prefetch for huge folders, on-demand download with watchdog, keep/free up space, Explorer Status column and verbs.

## [0.1.0] - 2026-11-13 (milestone)
- Feasibility spike (P0): Native AOT on .NET 11, unpackaged Cloud Files provider, 500k-item index, responsiveness harness. Verdict: go.
