# Reference: abraunegg/onedrive (Linux/FreeBSD/OpenBSD client)

- Repo: https://github.com/abraunegg/onedrive (GPL-3.0). Its author shared it with the user (2026-11-18) and warned that the OneDrive API is "particular".
- **Licence rule:** read for behaviour and design decisions only. Do NOT copy or transliterate code (NoDrive is MIT). Write our own implementation and cite the behaviour, not the code.
- Use it when NoDrive does something differently, or when OneDrive behaves in a way we cannot explain.

## Topics to study first (each answers an open NoDrive question)
1. Upload sessions: how it persists and resumes them, how it handles 409/416 on a fragment, the ghost/empty item an abandoned session leaves, session expiry. (Our Wi-Fi cut bug, see `activeContext.md` 2026-11-17/18.)
2. eTag / cTag behaviour after upload (OneDrive re-processing media changes the eTag), and how it decides "changed in the cloud" before a delete or update.
3. Delta quirks: duplicates, out-of-order items, items without hashes, 410 resync, deleted items without metadata, the consumer/business differences it documents.
4. Name and path rules (invalid characters, case collisions, trailing dots) and how it reports them.
5. Throttling (429 / Retry-After) and its own back-off, plus how it uses webhooks/websocket notifications versus polling.
6. Its known-issues and troubleshooting documents (a catalogue of API surprises).

## Findings
### 2026-11-18, upload sessions (subagent read of the master source; line numbers may drift, issues not searched)
- **Same design as ours:** saves the session as a JSON file (url, expiry, ranges, local path, size, mtime, target ids), re-saved after every fragment; on startup it resumes only if size and mtime match exactly and a GET on uploadUrl still answers with `nextExpectedRanges`; empty ranges = already complete; a discarded session is cancelled with DELETE uploadUrl. No hash check before resuming.
- **Confirms our ghost theory:** a code comment says an aborted session on OneDrive Personal can appear as a zero-byte remote placeholder, which is why they cancel explicitly. They do NOT detect or delete an existing zero-byte item and do not handle nameAlreadyExists; they send conflictBehavior=replace on sessions (we use fail to never overwrite, hence our ghost cleanup).
- Fragment errors: 404 -> new session; 403 (tempauth expired, independent of expirationDateTime) -> new session from 0; 416 -> re-PUT same offset (no status query; ours asks the session first); 409 -> no special handling; others one retry. Retry waits: 429/408 Retry-After, 5xx flat 30 s, others exponential up to 120 s; retry limit about a year (ours is bounded).
- Fragment size default 10 MiB (10-60 MiB, multiple of 320 KiB); ours is 5 MiB.
- Completion: the final response is the DriveItem; they compare size and quickXorHash with the local hash computed while streaming; mismatch only warns. No recovery found for a timeout on the LAST fragment (ours: hash adoption).
- Personal quirks: session creation sends fileSystemInfo.lastModifiedDateTime (whole seconds) so the server keeps the local mtime; simple uploads cannot carry it, so the local mtime is set to the server's; zero-byte files always use a simple PUT (OneDrive/onedrive-api-docs issue 53); `If-Match` on session creation was once disabled because the eTag changes after creation (412 then 416 on the last fragment), now sent with one retry.
- **Done in NoDrive from this:** fragment 403/404/401 forgets the saved session and retries with a new one; diagnostic hash check after the first 50 uploads of a run (log "hash check ok / MISMATCH") to validate our quickXorHash against OneDrive's.
- **Ideas not done:** send `fileSystemInfo.lastModifiedDateTime` at session creation so the cloud keeps our mtime (would remove the post-upload "touch" and the spurious second upload); 10 MiB fragments; check `expirationDateTime` locally.
