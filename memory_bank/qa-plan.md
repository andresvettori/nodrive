# QA plan (beta)

## Severity

| Level | Meaning | Examples | Rule |
|---|---|---|---|
| S1 | Data loss, data exposure, or Explorer/PC hang | a file deleted or overwritten in the cloud by mistake, Explorer frozen, memory runaway | Stop the line: fix before any further release |
| S2 | A main feature is broken or unreliable | uploads never finish, folder shows wrong contents, sign-in loop, installer fails | Fix before the next beta; no release candidate with an open S2 |
| S3 | A feature works with a visible flaw or needs a workaround | wrong icon, odd message, slow folder | Fix when convenient |
| S4 | Cosmetic or wording | layout, translation | Backlog |

## Exit criteria

- **Beta (friends and family):** feature complete; S1/S2 empty; scenario matrix passed; soak passed; support report and guides ready; upgrade tested.
- **Release candidate:** two testers used it daily for a week with no S1/S2; code-signed installer; docs and changelog final.
- **Stable:** one RC period without new S1/S2.

## Scenario matrix (run on the AOT build from CI; record build, date, result)

| Area | Scenarios |
|---|---|
| Install | Fresh install; install with `AUTOSTART=0`; upgrade over the previous release; uninstall with nothing pending; uninstall while an upload is queued (must refuse); reinstall over existing files (no duplicates, no empty folders); install on a PC with OneDrive running and Folder Backup on |
| Browse and download | Open folders of 10, 1,000 and 30,000 items; open a file with Photos, Paint, Office, PDF viewer; open a 1 GB file (progress dialog); keep on device; free up space; thumbnails view |
| Upload and edit | New file and folder; 10 MB and 1 GB file; same-name file (conflict copy); edit a downloaded Office file and save (safe-save merge); edit while the cloud copy changed (conflict copy); rename and move; delete and restore from the OneDrive recycle bin |
| Offline and recovery | No internet at start; internet lost during upload and download; PC sleep and resume; reboot with work queued; kill the engine (task manager) during upload and during a delta page; edits, deletes and renames done while NoDrive was stopped; wipe a folder while stopped (must be held, not deleted) |
| Resources | 24 h soak (memory flat, no stuck requests, log has no repeating errors); disk almost full; low bandwidth limit; 500k-item index memory |
| Names | Names with accents, emoji, trailing dot, reserved names, case-only differences, very long paths |
| Coexistence | OneDrive running side by side; Switch assistant scan; take-over on a test folder |
| Support | Report a problem with and without names; `--doctor`; the Support ID in the log matches the report; guides open (English and Spanish) |
| Platform | Windows 11 24H2 and 25H2, fresh VM, standard (non-admin) user, Defender on, high DPI, Spanish and English Windows |

## Tracking

Bugs go to GitHub issues with the bug template (version, steps, severity, report zip). Each test round is a short note in `activeContext.md` (build, what was run, S1-S4 found). Known gaps stay in `progress.md`.
