# Contributing to NoDrive

Thank you for helping. NoDrive is a very lightweight OneDrive (personal account) client for Windows 11 written in C# / .NET with Native AOT and the Windows Cloud Files API. It is a one-maintainer project, so small, focused contributions that follow the rules below are the easiest to review and merge. By contributing you agree that your work is released under the project's [MIT licence](LICENSE).

## Before you start

- **Bugs:** use the issue template and attach the support zip (tray icon > **Report a problem...**, with the option that leaves file names out). See [docs/report-a-problem.en.md](docs/report-a-problem.en.md). Say the build number.
- **Features and larger changes:** open an issue first and describe the problem, not only the solution. Some things are decided on purpose (see the "Explicit non-goals" in [memory_bank/projectbrief.md](memory_bank/projectbrief.md) and the ADRs in [memory_bank/decisions/](memory_bank/decisions/)).
- **Security problems:** do not open an issue; follow [SECURITY.md](SECURITY.md).

## The rules that are never traded for a feature

These come from [CLAUDE.md](CLAUDE.md) and [memory_bank/bestPractices.md](memory_bank/bestPractices.md):

1. **Never hang or block File Explorer.** No I/O inside a Cloud Files callback: validate, enqueue, return. Every pending request has a watchdog and is completed well inside Windows' 60 s limit.
2. **Memory stays small and independent of the number of items** (design target: 500,000). Bounded queues, bounded concurrency, buffers released after bursts.
3. **No data loss:** journal before mutating, idempotent handlers, `If-Match` on uploads, explicit `conflictBehavior`, conflict copies instead of overwrites, the mass-delete guard, and never dehydrate a file that is not in sync.
4. **Native AOT clean:** no IL2xxx/IL3xxx warnings from our code, source-generated JSON and P/Invoke only.

A change that breaks one of these will not be merged, however useful it is.

## Build and test

You need the .NET SDK pinned in [global.json](global.json). The solution builds and its tests run on macOS, Linux and Windows; only the Windows engine can be run and AOT-published on Windows.

```
dotnet build
dotnet test
dotnet publish src/NoDrive.Engine -c Release -r win-x64 -p:PublishAot=true   # Windows only
```

Test the real engine with the **CI Native AOT exe or the MSI**, not a local unsigned JIT build (Windows may block it). CI publishes both as artifacts for every commit. More detail is in the README ("Build and test") and [memory_bank/techContext.md](memory_bank/techContext.md).

## Code and commits

- Platform-neutral logic belongs in `NoDrive.Core` or `NoDrive.Graph`, where it can be unit tested without Windows. Put decisions (for example "is this an edit?", "is this a name clash?") in small pure functions and test them; keep Windows-only code thin.
- Public APIs and every cfapi or Graph call carry a short comment about threading and timing constraints.
- Add tests with the change. Prefer a fake Graph (see `tests/NoDrive.Core.Tests`) over guessing how the service behaves. When a bug depends on how OneDrive behaves, write down what you observed (log lines, request and response) in the issue or the PR.
- Small commits with conventional prefixes: `feat`, `fix`, `docs`, `test`, `chore`, `perf`, `refactor`.
- Update [CHANGELOG.md](CHANGELOG.md) for anything a user would notice, and the guides in `docs/` (English and Spanish) when behaviour or settings change.

## Project memory

[memory_bank/](memory_bank/) is the project's long-term memory (scope, architecture, decisions, progress). If your change alters architecture or a decision, update the relevant file or add a decision record; the maintainer reviews it with the code.

## Third-party code and licences

NoDrive is MIT. Use only permissive code (MIT, BSD, Apache-2.0, 0BSD) and keep the notice. **Do not copy or translate code from GPL or LGPL projects**, including other OneDrive clients; reading them to understand how OneDrive behaves is fine, and what we learned is recorded in [memory_bank/research/](memory_bank/research/). If you used an AI assistant, you are still responsible for the result: review it, test it, and make sure it does not reproduce licensed code.

## Pull requests

- One topic per PR, with a short description of the problem, the change and how you tested it (including what you did on a real Windows machine, if relevant).
- Keep the build green: `dotnet build` with no new warnings and `dotnet test` passing.
- Be patient: review happens when the maintainer has time.
