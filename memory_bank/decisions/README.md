# Architecture Decision Records

One ADR per default decision taken in the approved plan of 2026-11-13. ADRs are immutable once Accepted; a changed decision gets a new ADR that supersedes the old one (old Status becomes "Superseded by ADR-00xx"). Read them after `projectbrief.md` and `systemPatterns.md`; each ADR cites the research evidence (versions, limits, URLs) that justified it, so it can be re-checked when the platform moves.

## Index

| ADR | Title | Status |
|---|---|---|
| [ADR-0001](ADR-0001.md) | Windows Cloud Files API (cfapi) over a custom Explorer-like client or a WinFsp/Dokany mount | Accepted 2026-11-13 |
| [ADR-0002](ADR-0002.md) | .NET 11 + Native AOT for the engine, with fallback to .NET 10 LTS / trimmed JIT (kill criteria) | Accepted 2026-11-13 |
| [ADR-0003](ADR-0003.md) | Hand-rolled authorization-code + PKCE with system browser, loopback listener and DPAPI; MSAL/WAM deferred | Accepted 2026-11-13 |
| [ADR-0004](ADR-0004.md) | Hand-written Graph client (HttpClient + System.Text.Json source-gen) instead of the Microsoft.Graph SDK | Accepted 2026-11-13 |
| [ADR-0005](ADR-0005.md) | SQLite via the system `winsqlite3.dll` as the disk-backed index and journal | Accepted 2026-11-13 |
| [ADR-0006](ADR-0006.md) | MIT licence for NoDrive; permissive sources only; GPL projects as read-only references | Accepted 2026-11-13 |
| [ADR-0007](ADR-0007.md) | Lazy population (PopulationPolicy PARTIAL + FETCH_PLACEHOLDERS from the index) and PROGRESSIVE hydration for a 500k-item drive | Accepted 2026-11-13 |
| [ADR-0009](ADR-0009.md) | Explorer verbs via writing SyncRootManager Flags=1058 after Register (no elevation needed, measured); engine never re-registers an existing root | Accepted 2026-11-14 (provisional) |
| [ADR-0010](ADR-0010.md) | Full hydration policy (whole file on first access) by default | Accepted 2026-11-15 |
| [ADR-0011](ADR-0011.md) | Settings and activity UI as a local web page served by the engine | Accepted 2026-11-15 |
| [ADR-0012](ADR-0012.md) | WiX v5 per-user MSI installer (MSIX later) | Accepted 2026-11-16 |
| [ADR-0013](ADR-0013.md) | Coexistence with the OneDrive app; adoption of identical files by name, size and modified time | Accepted 2026-11-16 (amends ADR-0008) |
| [ADR-0008](ADR-0008.md) | Coexistence with OneDrive.exe: detection of a linked personal account and its folder, refuse-to-run rule, adoption of the existing sync folder | Accepted 2026-11-13 |

## Conventions

- File name: `ADR-NNNN.md`, four digits, sequential, never reused.
- Status values: Proposed, Accepted, Deprecated, Superseded by ADR-NNNN.
- Every factual claim about an API, limit, version or bug carries a source URL, or is marked "community-reported" (Microsoft Q&A answers by external vendor moderators, forum threads, blog measurements) or "to verify in spike".
- "Revisit when" lists concrete, observable triggers; an ADR is re-examined when one fires, not on a schedule.
- Hard requirement that every ADR must respect: NoDrive never hangs or blocks File Explorer, never leaves a dialog stuck, never balloons memory. Responsiveness and correctness outrank features.

## Template

```markdown
# ADR-NNNN: <short title>

## Status

Accepted YYYY-MM-DD

## Context

What problem is being decided, the constraints, and the evidence (versions, limits, bugs, measurements) with a source URL per claim. Alternatives considered, each with the reason it lost.

## Decision

The choice, stated in one or two sentences, followed by the concrete rules that follow from it (what we do, what we never do).

## Consequences

### Positive

### Negative

### Revisit when

Observable triggers that reopen this decision.

## Sources

- URL list (primary sources first; secondary or community sources labelled).

## Date

YYYY-MM-DD
```
