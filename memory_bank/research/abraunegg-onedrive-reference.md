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
(none yet; add dated entries with the file or document consulted and what NoDrive should do differently)
