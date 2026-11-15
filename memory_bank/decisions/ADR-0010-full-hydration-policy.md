# ADR-0010: Full hydration policy by default (supersedes PROGRESSIVE in ADR/techContext)

Status: accepted 2026-11-15. Measured on the user's Windows 11 PC.

## Context
With PROGRESSIVE, Photos (packaged app) read small ranges of an online-only JPG and Windows cancelled those reads from the app side 3 ms to 1.4 s after they started (CANCEL_FETCH_DATA with the exact read range; our handler only forwarded it). Photos then reported "file corrupted"; Paint and "Always keep" worked. OneDrive opens the same file with a "Downloading file" dialog and then Photos shows it. A provider-side whole-file fetch on first read was not fast enough (Graph URL lookup plus download exceeded the app's deadline).

## Decision
Register `StorageProviderHydrationPolicy.Full` (AutoDehydrationAllowed modifier). Windows downloads the whole file on first access, shows its own progress dialog, and apps only see a complete file. Registration records the choice in the sync root key value `NoDriveHydration`; a changed choice re-registers once and reapplies Flags 1058. `NODRIVE_HYDRATION=progressive` switches back.

## Consequences
- Large videos download completely before playing (same as OneDrive); the earlier Photos video-streaming problem is moot for Photos.
- Read-ahead and the small-file whole mode in CloudProvider become mostly redundant; keep as fallback for the progressive switch.
- Watch: Explorer thumbnail generation may hydrate whole photos; verify bandwidth and memory (to do).
