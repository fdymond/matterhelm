# CLAUDE.md — orchestration playbook

This repo is built by AI sub-agents under an integrator. Read this first, then
`docs/BLUEPRINT.md` (design authority) and `docs/ENGINEERING-STANDARDS.md`
(quality bar). `BACKLOG.md` is the work queue; `docs/DEVELOPMENT-PLAN.md`
defines Done.

`AGENTS.md` points here; this file remains the binding agent playbook.

## Ground rules for every agent

1. **One story per agent.** Your assignment names a story ID; its acceptance
   criteria are your contract. Discoveries outside scope → note them in your
   report as proposed backlog items; do not implement them.
2. **File ownership is exclusive.** The delegation prompt lists the files you
   own. Touch nothing else — parallel agents own the rest. If you believe you
   need a file you don't own, stop and say so in your report.
3. **Evidence or it didn't happen.** Paste verbatim `npm run verify` / test
   output into your final report. DoD requires it.
4. **Never** run `git commit/checkout/reset/stash` unless your prompt says so.
   The integrator owns git. Never push. Never touch `%APPDATA%` state, and
   never reference or depend on anything outside this repo — the product is
   fully standalone by decision (ADR-001).
5. **Deviation = ADR.** If the blueprint is wrong or matter.js reality differs,
   write the one-page ADR draft in `docs/adr/` and flag it prominently — don't
   silently improvise architecture.
6. Zero warnings. Match surrounding code style. Delete rather than comment out.

## Commands

```bash
cd bridge
npm ci                 # exact deps
npm run verify         # lint + typecheck + tests — THE merge bar
npm start              # run bridge (dev)
npm test -- --watch    # tdd loop
npm test -- src/mapping/actions.test.ts   # single test file (vitest)
npm test -- -t "volume"                   # tests matching a name
```

Node 22.13+ LTS; `@matter/main` is pinned at 0.17.7. Re-verify ADR-010 on every
matter.js upgrade. dotnet is at `"C:\Program Files\dotnet\dotnet.exe"`; the
tray app builds with
`dotnet build app/MatterHelm/MatterHelm.csproj -c Release`
and tests with
`dotnet test app/MatterHelm.Tests/MatterHelm.Tests.csproj -c Release`
(warnings-as-errors). Demo harnesses run from
`app/MatterHelm/bin/Release/net10.0-windows10.0.17763.0/MatterHelm.exe`.
This product is fully standalone (ADR-001): never
reference, read config from, or depend on code outside this repository.

**Current state**: v0.7.1 ships the completed Sprint 11 product; the bridge and
tray app are implemented and ship together. Current test counts are published
by CI; app line coverage is about 58 % with a 45 % CI floor. `npm run verify`
and the .NET build/test commands remain live merge gates. Both C# projects target
`net10.0-windows10.0.17763.0` (Windows 10 1809+ x64); re-read the projects at
release time before stating that floor in public documentation.

## Architecture invariants (enforced in review)

- `matter/` ↛ `ipc/` (and vice versa); they meet in `index.ts` via `mapping/`.
- matter.js types stay behind `matter/adapter.ts`; protocol types behind
  `ipc/protocol.ts`; both boundaries are the ONLY places their packages are
  imported.
- All inbound IPC frames are parsed at their boundary (zod in the bridge,
  typed records in the app); all trust boundaries are validated.
- Pure logic (`mapping/`, protocol schemas) has ≥ 90 % test coverage.
- IPC stays on the `localhost` loopback only: the tray explicitly rejects a
  non-loopback remote endpoint before client-slot admission; token via env;
  token never logged/persisted; either peer closes on its first invalid frame.
- `BridgeHost` remains the app façade/composition root; lifecycle, action/macro
  dispatch, and state publication belong in `BridgeLifecycleCoordinator`,
  `BridgeActionDispatcher`, and `VolumeStatePublisher`. `MediaKeys` remains a
  façade over `FocusedMediaRouter` and `WindowsForegroundMediaCommandSender`.
- Normative specs: device model = BLUEPRINT §2.2, IPC frames = BLUEPRINT §2.3.
  `app/.../Sidecar/Protocol.cs` must mirror `bridge/src/ipc/protocol.ts`
  exactly — changing one side without the other is protocol drift (review
  blocker); breaking changes bump `protocol` in `hello` + need an ADR.

## Integrator workflow (main session)

1. Pick ready stories from BACKLOG (deps done, files disjoint) → delegate in
   parallel with explicit file ownership + story text + this file's rules.
2. On reports: verify evidence, run `npm run verify` fresh, adversarial-review
   sprint tails (S*-R stories) with a different model than wrote the code.
3. Merge = conventional commit per story on `main`; update BACKLOG story
   status; keep README "Status" honest.
4. Hardware-in-the-loop stories (S0-3/S0-4, E2E) require the human for phone /
   Google Home app steps — schedule them explicitly, never fake the evidence.
