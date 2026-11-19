# Security policy

NoDrive reads and writes your OneDrive files, so security reports are taken seriously. NoDrive is a single-maintainer project: replies are best effort, but a report that is clear and reproducible will be looked at quickly.

## Supported versions

Only the **latest beta release** receives fixes (see [Releases](../../releases) and [CHANGELOG.md](CHANGELOG.md)). Please update before reporting and say which build you tested: the build number is at the bottom of the NoDrive page, in the tray tooltip and in `NoDrive.Engine.exe --version`.

## How to report a vulnerability

**Do not open a public issue** for a security problem.

Use GitHub's private reporting: open the repository's **Security** tab, choose **Report a vulnerability**, and describe the problem there. Only the maintainer can see it.

Please include:

- what you found and why it matters (what an attacker can read, change or delete),
- the NoDrive build and Windows version,
- exact steps or a small proof of concept,
- whether it needs local access, a malicious file or folder name, a network position, or another program on the same PC.

Do **not** send access tokens, your `token.bin`, full logs or your files. If you attach a support bundle, create it with the option that leaves file names out (tray icon > Report a problem...).

## What to expect

- An acknowledgement, normally within 7 days.
- A first assessment (accepted, needs more information, or not a vulnerability), normally within 14 days.
- A fix in a new beta build for confirmed issues, with credit in the changelog if you want it. Severe issues (data loss, token theft, remote code execution) are handled first.
- Please keep the details private until a fixed build is available. Good-faith research on your own account and your own PC will not be treated as a violation of this policy.

## What is in scope

- Sign-in and token handling (OAuth with PKCE, the token file protected with Windows DPAPI).
- The local settings page (`127.0.0.1`, protected by a per-session token cookie, host check and a custom header): anything that lets a web page or another local user drive it.
- Data safety in the sync engine: a way to make NoDrive delete, overwrite or upload files it should not (path traversal through cloud file names, mass-delete guard bypass, conflict-copy bypass).
- The installer and the update path (what the MSI installs, runs and removes).
- Information leaks: logs, activity files or support bundles that contain secrets.

## What is out of scope

- Problems that need an already-compromised Windows account or administrator rights.
- Weaknesses in Windows, OneDrive or Microsoft Graph themselves (report those to Microsoft).
- The fact that builds are not yet code-signed and Windows Smart App Control may block them (known; see [docs/troubleshooting.en.md](docs/troubleshooting.en.md)).
- Denial of service by filling your own disk or OneDrive.

## How NoDrive limits risk

- It asks only for the Microsoft Graph permissions it needs (`Files.ReadWrite`, `offline_access`) and sends nothing to anyone but Microsoft.
- No telemetry. Logs and the activity journal stay in `%LOCALAPPDATA%\NoDrive`.
- Remote deletes go to the OneDrive recycle bin, every upload carries an `If-Match` version check, and a burst of deletions is held until you allow it.
