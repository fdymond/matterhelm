# Contributing to MatterHelm

MatterHelm is currently a private, solo-maintained repository built with AI
sub-agents under human review (see `CLAUDE.md` for that internal workflow).
This document describes the workflow any contributor — human or agent —
follows once the repo opens up, and it is also the accurate description of
how the code has been built so far.

## Prerequisites

- **Node.js 22.13+** (LTS) — for `bridge/`.
- **.NET 10 SDK** — for `app/` (WinForms tray app, Windows-only).
- Windows for `app/` work (WinForms + Win32 P/Invoke); `bridge/` is
  OS-neutral and CI runs it on both `ubuntu-latest` and `windows-latest`.

## Build & test

```bash
# bridge (Node/TypeScript sidecar)
cd bridge
npm ci                  # exact pinned deps
npm run verify          # lint + typecheck + tests — the merge bar
npm run coverage         # vitest with coverage (mapping/ + ipc/protocol.ts + config.ts ≥ 90% lines)
npm run bundle           # esbuild single-file bundle -> dist/bridge.cjs

# app (C#/.NET tray application)
dotnet build app/MatterHelm/MatterHelm.csproj -c Release
dotnet test app/MatterHelm.Tests/MatterHelm.Tests.csproj -c Release
```

Both builds are **warnings-as-errors** / **zero-warnings**. A warning is a
decision postponed, and postponed decisions don't merge.

### Demo suite — acceptance evidence, not just unit tests

Several stories are only meaningfully verified end-to-end, so the app ships
scripted, self-checking demo harnesses (exit 0 = pass) invoked as flags on the
built exe, e.g.:

```bash
app/MatterHelm/bin/Release/net10.0-windows/MatterHelm.exe --demo-wired
app/MatterHelm/bin/Release/net10.0-windows/MatterHelm.exe --demo-overlay
app/MatterHelm/bin/Release/net10.0-windows/MatterHelm.exe --demo-pairing-window
app/MatterHelm/bin/Release/net10.0-windows/MatterHelm.exe --demo-settings-window
app/MatterHelm/bin/Release/net10.0-windows/MatterHelm.exe --demo-sidecar-chaos
```

If your change touches UI, wiring between the sidecar and the executor, or
config/protocol plumbing, re-run the relevant demo(s) and paste the exit
code / output in your PR — this is required acceptance evidence alongside
`npm run verify` / `dotnet test`, not optional polish.

## The engineering bar

Read `docs/ENGINEERING-STANDARDS.md` before writing code — it is the quality
bar this project is held to, not a suggestion. In short:

- **Zero warnings** is the merge bar (`npm run verify`, `dotnet build`
  warnings-as-errors) — never suppress to get green, fix the cause.
- **Parse, don't validate.** Every trust boundary (config file, every inbound
  IPC frame) is `zod`-validated on the bridge side and typed-record-validated
  on the app side; don't add an `any`/unchecked cast to route around it.
- **Evidence over confidence.** A change is done when its test/demo output is
  in the PR — not when it "should work". Paste verbatim command output.
- Pure logic (`bridge/src/mapping/`, `ipc/protocol.ts`) stays at ≥ 90% line
  coverage — there's no coverage excuse for code with no I/O.
- Small, sharp module boundaries: `matter/` and `ipc/` never import each
  other directly (see "Protocol parity" below); matter.js types stay behind
  `matter/adapter.ts`.

## Commit style

This repo uses [Conventional Commits](https://www.conventionalcommits.org/)
throughout its history — `feat:`, `fix:`, `docs:`, `test:`, `refactor:`,
`chore:`, imperative subject ≤ 72 chars, body explains *why*. Examples from
the actual log:

```
feat(app): settings window with nav, search, custom-command CRUD (S4-3)
fix(app): S4-R review findings
refactor(app): ADR-005 modernization at the Recommended analyzer bar (S4-4)
```

## One story per PR, adversarial review

Work is scoped to one backlog story (see `BACKLOG.md` / `docs/DEVELOPMENT-PLAN.md`)
per PR, with acceptance criteria as the contract. Every implementation PR is
expected to get an adversarial review pass — a reviewer actively hunting for
correctness bugs, races, protocol drift, and scope creep, not a rubber stamp.
Findings get verified before being fixed (no plausible-but-wrong churn).
Discoveries outside a PR's scope are noted as proposed follow-up items, not
folded silently into the current change.

## Protocol parity — bridge ↔ app must move together

`bridge/src/ipc/protocol.ts` and `app/MatterHelm/Sidecar/Protocol.cs` define
the same wire protocol and must mirror each other exactly. Changing one
without the other is protocol drift and is a review blocker. A breaking
protocol change bumps the `protocol`/`v` version in the `hello` frame on
**both** sides and needs an ADR explaining the change (see below).

## Architecture Decision Records

Any deviation from `docs/BLUEPRINT.md` (the binding architecture/protocol
spec), or any non-obvious architectural choice, gets a one-page ADR in
`docs/adr/` using `docs/adr/000-template.md` (`NNN-title.md`: Context,
Decision, Consequences). Don't silently improvise architecture — write it
down so the next agent or reviewer has the "why."

## Filing issues / proposing changes

Use the GitHub issue templates (bug report / feature request) once the repo
is public. Until then, discuss scope with the maintainer before starting
non-trivial work — file ownership across concurrent efforts is expected to
stay disjoint (see `CLAUDE.md` ground rule 2).
