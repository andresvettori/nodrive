# NoDrive

Lightweight OneDrive (personal) client for Windows 11. C# / .NET 11 / Native AOT. File Explorer
integration through the Windows Cloud Files API (placeholders, on-demand hydration). Single
developer project. Licence: MIT.

## Memory bank (read first, every task)

`memory_bank/` is the project's long-term memory, modelled on the Cline memory bank. At the start of
EVERY task read all of these, in this order:

1. `memory_bank/projectbrief.md` - scope, goals, hard requirements (source of truth)
2. `memory_bank/productContext.md` - why NoDrive exists, UX goals
3. `memory_bank/systemPatterns.md` - architecture, critical paths, safety rules
4. `memory_bank/techContext.md` - stack, versions, build/publish commands, constraints
5. `memory_bank/activeContext.md` - current focus, recent changes, next steps
6. `memory_bank/progress.md` - what works, what is left, known issues
7. `memory_bank/p0-results.md` (P0 findings, go/no-go and the design changes they imply), `memory_bank/projectPlan.md`, `memory_bank/p0-runbook.md`, `memory_bank/bestPractices.md`, `memory_bank/decisions/` (ADRs),
   `memory_bank/research/` as the task requires.

Update rules:
- After every work session or significant change: update `activeContext.md` (focus, recent changes,
  next steps, session log) and `progress.md` (status, checkboxes, known issues).
- When an architectural or technology decision changes: add or supersede an ADR in
  `memory_bank/decisions/` and update `systemPatterns.md` / `techContext.md`.
- When the user says "update memory bank": review EVERY file in `memory_bank/` and bring it in line
  with the code and the current plan.
- New durable knowledge (API quirks, measurements, platform bugs) goes into `memory_bank/research/`
  with a source URL or a "measured on <date>" note.

## Hard requirements (never trade these for features)

- NoDrive must NEVER hang or block File Explorer, leave copy/move dialogs stuck, or make Explorer
  unresponsive. No I/O inside a Cloud Files callback: validate, enqueue, return. Every pending
  request has a watchdog and is completed (success or failure status) well inside the 60 s limit.
- Memory must stay minimal at idle and independent of item count (design target: 500k items).
  Release memory after bursts. Bounded concurrency, queues and buffers everywhere.
- No data loss: journal before mutate, idempotent handlers, If-Match on every upload, explicit
  conflictBehavior, conflict copies instead of overwrites, mass-delete threshold, never dehydrate a
  file that is not in sync.
- Native AOT clean: zero IL2xxx/IL3xxx warnings from our code; source-generated JSON and P/Invoke only.

See `memory_bank/bestPractices.md` for the full rule set.

## Build and test

Windows TFM projects restore and build on macOS/Linux (EnableWindowsTargeting) but only run and
AOT-publish on Windows.

```
dotnet build                                   # whole solution
dotnet test                                    # cross-platform unit tests (tests/)
dotnet publish src/NoDrive.Engine -c Release -r win-x64 -p:PublishAot=true   # Windows only
```

## Layout

- `src/NoDrive.Core` - reconciler, index, state machine, abstractions (platform neutral, testable anywhere)
- `src/NoDrive.Graph` - OneDrive via Microsoft Graph: PKCE auth, DTOs, delta, download, upload, quickXorHash
- `src/NoDrive.Engine` - the always-on Windows process: tray, Cloud Files provider, watcher, supervisor
- `tests/` - unit and property tests with a fake Graph server
- `spikes/` - P0 measurement and feasibility apps (see memory_bank/projectPlan.md)
- `memory_bank/` - project memory (see above)

## Conventions

- Small commits with conventional-commit prefixes (feat, fix, docs, test, chore, perf, refactor).
- Public APIs and all cfapi/Graph interop carry a short doc comment stating threading and timing
  constraints.
- Windows-only code lives in `NoDrive.Engine` or behind an interface implemented there; `Core` and
  `Graph` stay buildable and testable on macOS/Linux.
