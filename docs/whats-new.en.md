# What's new

## 0.9.0 beta (November 2026)

The first beta. NoDrive now handles the day-to-day things that used to go wrong.

- **Your edits are saved to OneDrive.** Change a file you downloaded and NoDrive uploads the new version. If the cloud copy changed first, nothing is overwritten: your version is saved next to it as a "NoDrive conflict" copy.
- **NoDrive catches up after being stopped.** If it was closed, crashed or the PC restarted, it finds what you changed meanwhile (new, edited, renamed, moved or deleted files). If a lot of files are suddenly missing, it asks you before deleting anything in the cloud.
- **Sleep and network.** When the PC wakes up or the internet comes back, NoDrive checks right away.
- **Safer.** Requests that get stuck are failed quickly so Explorer never freezes, a damaged database is rebuilt, and downloads stop with a clear message when the disk is almost full.
- **Report a problem.** Tray icon > Report a problem... creates one file you can send us. See the report guide.
- **Versions.** The version is shown at the bottom of the NoDrive page, in the tray tooltip and in the installer's file name.

- **Large uploads survive network cuts.** If the connection drops (or the PC sleeps or NoDrive restarts) in the middle of a big upload, NoDrive continues where it stopped instead of starting over or creating a "conflict" copy.
- **Faster big folders.** A new setting, Bulk download speed (Gentle, Balanced, Turbo), controls how many files "Always keep on this device" downloads at once.

Known limits: the installer is not signed yet, so Windows may warn; online-only pictures show an icon instead of a preview until downloaded.

Earlier milestones and technical details: see CHANGELOG.md in the project.
