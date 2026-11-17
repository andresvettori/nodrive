# Release process and versioning policy

## Version format

| Where | Example | Rule |
|---|---|---|
| Display version | `0.9.0-beta.142` | `<MAJOR>.<MINOR>.<PATCH>-<channel>.<build>`; a stable release is just `1.0.0` |
| Exe file version | `0.9.0.142` | `<MAJOR>.<MINOR>.<PATCH>.<build>` (build at most 65535) |
| MSI version | `0.9.142` | `<MAJOR>.<MINOR>.<build>`; Windows Installer upgrades by this, so it always grows |
| Git tag | `v0.9.0-beta.142` | the display version with `v` |
| Installer file | `NoDrive-0.9.0-beta.142-x64.msi` | plus `SHA256SUMS.txt`; `NoDrive-x64.msi` is a convenience alias of the latest CI build |

- **Source of truth:** `eng/Versioning.props` (`NoDriveBaseVersion`, `NoDriveChannel`). CI supplies `NoDriveBuild` (the workflow run number, or the number in a release tag) and `NoDriveCommit`. Local builds show `0.9.0-beta.0 (local)`.
- **Bumps:** MAJOR for incompatible data or behaviour changes, MINOR for new features, PATCH for fixes only. The channel moves `beta` -> `rc` -> stable (empty channel). The build number only grows within a channel.
- **Where users and support see it:** page footer (with the commit and the Support ID), tray tooltip, Apps list (comments), exe properties, installer file name, log header, `--version`, `--doctor`, the support report.
- **Constants, not reflection:** the engine reads the version from a generated `BuildInfo` class (reflection on assembly attributes is unreliable under Native AOT).

## Cutting a release

1. Make sure `CHANGELOG.md` has a section `## [<version>-<channel>] - <date>` (for example `[0.9.0-beta]`) and `docs/whats-new.*.md` summarises it for users (English and Spanish). Update README status and `memory_bank/progress.md`.
2. Pick the commit and its build number (from the page footer, `BUILD.txt` on `ci-binaries`, or the workflow run number). Run the QA gate below.
3. Tag and push: `git tag v0.9.0-beta.142 <commit> && git push origin v0.9.0-beta.142`.
4. The `windows` workflow rebuilds that commit with exactly that version, builds the MSI, creates a GitHub release (marked pre-release when there is a channel) with the MSI, the SHA-256 and the notes from `CHANGELOG.md` (`tools/release-notes.ps1`).
5. Install the released MSI over the previous one on the test PC (upgrade path), then give friends and family the link and the guides (`docs/install.*`, `docs/report-a-problem.*`).

## Upgrade and downgrade rules

- The MSI is a MajorUpgrade: install the newer MSI over the old one; NoDrive quits, files are replaced, sync folder and settings stay.
- The index schema only moves forward (currently version 4, migrated in place). Downgrading needs an uninstall first; after a downgrade delete `%LOCALAPPDATA%\NoDrive\index.db*` (it is rebuilt from the cloud; placeholders on disk are kept). The installer refuses a downgrade by design.
- Never reuse or move a published tag. A bad release is fixed by a new build number.

## QA gate (details in `qa-plan.md`)

A beta build may be tagged when: no open S1 or S2 bug; the scenario matrix in `qa-plan.md` passed on the current build (or its known gaps are written in the release notes); a 24 h soak ran on the exact AOT build with memory flat; the upgrade from the previous release was tested. A release candidate additionally needs one week of daily use by two people without S1/S2 reports, and code signing.
