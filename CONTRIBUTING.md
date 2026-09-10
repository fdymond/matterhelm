# Contributing to MatterHelm

MatterHelm welcomes contributions. It is maintained by one person in spare
time, so small, focused pull requests with complete evidence are the easiest
to review. Maintainers may use AI coding agents under human review (see
[`CLAUDE.md`](CLAUDE.md)); the engineering and evidence bar is identical for
every contribution.

## Prerequisites

- **Node.js 22.13+** (LTS) — for `bridge/`.
- **.NET 10 SDK** — for `app/` (WinForms tray app, Windows-only).
- Windows for `app/` work (WinForms + Win32 P/Invoke); `bridge/` is
  OS-neutral and CI runs it on both `ubuntu-latest` and `windows-latest`.

## Where things live

- `bridge/src/index.ts` composes the isolated `matter/`, `mapping/`, and
  `ipc/` layers. `ipc/client.ts` owns fail-closed WebSocket admission and
  reconnect; `matter/bridge.ts` owns Matter endpoints and bounded state writes.
- `app/MatterHelm/BridgeHost.cs` is the façade/composition root.
  `BridgeLifecycleCoordinator`, `BridgeActionDispatcher`, and
  `VolumeStatePublisher` own lifecycle, protocol-action dispatch/macros, and
  coalesced speaker-state publication respectively.
- `app/MatterHelm/Actions/MediaKeys.cs` is a façade over
  `FocusedMediaRouter` and `WindowsForegroundMediaCommandSender`.
  `Ui/SettingsCatalog.cs`, `BridgeRestartPolicy.cs`, `SettingLimits.cs`, and
  `Ui/KeySequenceCanonicalizer.cs` keep policy out of the WinForms shell;
  `Sidecar/SidecarLaunchSpec.cs`, `Sidecar/SidecarEnvironment.cs`, and
  `Infrastructure/SerialActionQueue.cs` own their named boundaries.

## Contribution workflow

1. Fork the repository, create a branch in your fork, and make the change
   there.
2. Scope the branch to one issue or one story from [`BACKLOG.md`](BACKLOG.md).
   Keep unrelated discoveries for a separate issue or proposed backlog item.
3. If work is happening concurrently, agree on disjoint file ownership before
   editing. Do not modify files assigned to another contributor.
4. Use [Conventional Commits](https://www.conventionalcommits.org/) for each
   commit.
5. Run the relevant build, test, coverage, and demo gates below.
6. Open a pull request and complete the
   [Definition of Done checklist](.github/PULL_REQUEST_TEMPLATE.md), including
   the exact command output and any relevant screenshots or demo output.

## Build & test

```bash
# bridge (Node/TypeScript sidecar)
cd bridge
npm ci                  # exact pinned deps
npm run verify          # lint + typecheck + tests — the merge bar
npm run coverage         # vitest coverage (mapping/, ipc/protocol.ts, config.ts, timing.ts, matter/diagnostics.ts ≥ 90% lines)
npm run bundle           # esbuild single-file bundle -> dist/bridge.cjs

# app (C#/.NET tray application)
dotnet build app/MatterHelm/MatterHelm.csproj -c Release
dotnet test app/MatterHelm.Tests/MatterHelm.Tests.csproj -c Release

# complete Windows distribution (strict Node SEA packaging by default)
./build.ps1
# explicit fallback to sidecar/node.exe + sidecar/bridge.cjs if SEA injection fails
./build.ps1 -AllowNodeLayoutFallback
```

Both builds are **warnings-as-errors** / **zero-warnings**. A warning is a
decision postponed, and postponed decisions don't merge.

`build.ps1` treats a `postject` failure as a build failure unless
`-AllowNodeLayoutFallback` is supplied. On Windows PowerShell 5.1, do not wrap
the script or its `postject` invocation in `2>&1`: native stderr can become a
terminating `ErrorRecord`; the script scopes this hazard internally and judges
`postject` by its process exit code.

### What CI runs

Pushes and pull requests run bridge verification on Windows and Ubuntu, plus a
Windows Release app build, tests, and coverage. The separate **Package** job
runs `./build.ps1 -SkipTests`, compiles the Inno Setup installer with version
`0.0.0`, and asserts that the notices and sidecar-layout manifest were emitted.

### Demo suite — acceptance evidence, not just unit tests

Several stories are only meaningfully verified end-to-end, so the app ships
scripted, self-checking demo harnesses (exit 0 = pass) invoked as flags on the
built exe, e.g.:

```bash
app/MatterHelm/bin/Release/net10.0-windows10.0.17763.0/MatterHelm.exe --demo-wired
app/MatterHelm/bin/Release/net10.0-windows10.0.17763.0/MatterHelm.exe --demo-overlay
app/MatterHelm/bin/Release/net10.0-windows10.0.17763.0/MatterHelm.exe --demo-pairing-window
app/MatterHelm/bin/Release/net10.0-windows10.0.17763.0/MatterHelm.exe --demo-settings-window
app/MatterHelm/bin/Release/net10.0-windows10.0.17763.0/MatterHelm.exe --demo-sidecar-chaos
app/MatterHelm/bin/Release/net10.0-windows10.0.17763.0/MatterHelm.exe --demo-welcome-window
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
without the other is protocol drift and is a review blocker. Additive message
evolution bumps `v` on both sides; a breaking change bumps `hello.protocol`
on both sides and needs an ADR explaining the change (see below).

## Architecture Decision Records

Any deviation from `docs/BLUEPRINT.md` (the binding architecture/protocol
spec), or any non-obvious architectural choice, gets a one-page ADR in
`docs/adr/` using `docs/adr/000-template.md` (`NNN-title.md`: Context,
Decision, Consequences). Don't silently improvise architecture — write it
down so the next agent or reviewer has the "why."

## Releases

A pushed `v*` tag is accepted only when the tag without its leading `v` exactly
matches both `<Version>` in `app/MatterHelm/MatterHelm.csproj` and `version` in
`bridge/package.json`; either mismatch fails before packaging. Release
concurrency is keyed by workflow and tag, and an in-progress tag build is never
cancelled. The release build is strict SEA packaging and
`THIRD-PARTY-NOTICES.txt` must cover the bundled npm packages, Node.js, QRCoder,
.NET runtime, WindowsDesktop runtime, and Windows SDK projection.

## Good first contributions

Start with the [`Proposed` section of BACKLOG.md](BACKLOG.md#proposed-from-agent-reports-integrator-triaged)
and open or comment on an issue before beginning non-trivial work. The
maintainer can confirm scope and avoid overlap with work already in progress.

## Filing issues / proposing changes

Use the GitHub issue templates for bug reports and feature requests. Search
existing issues first, describe the user impact, and keep one independently
reviewable change per issue and pull request.

## Licensing

The project does not require a Contributor License Agreement (CLA) or
Developer Certificate of Origin (DCO). Contributions are accepted under the
repository's [MIT License](LICENSE): inbound and outbound terms are MIT.
