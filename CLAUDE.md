# CLAUDE.md — orchestration playbook

This repo is built by AI sub-agents under an integrator. Read this first, then
`docs/BLUEPRINT.md` (design authority) and `docs/ENGINEERING-STANDARDS.md`
(quality bar). `BACKLOG.md` is the work queue; `docs/DEVELOPMENT-PLAN.md`
defines Done.

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
   The integrator owns git. Never push. Never touch `%APPDATA%` state or the
   sibling repo (`../windows-voice-control`) unless your story lives there.
5. **Deviation = ADR.** If the blueprint is wrong or matter.js reality differs,
   write the one-page ADR draft in `docs/adr/` and flag it prominently — don't
   silently improvise architecture.
6. Zero warnings. Match surrounding code style. Delete rather than comment out.

## Commands

```bash
npm ci                 # exact deps
npm run verify         # lint + typecheck + tests — THE merge bar
npm start              # run bridge (dev)
npm test -- --watch    # tdd loop
```

Node 22 LTS. On this machine dotnet (for Sprint-2 stories) is at
`"C:\Program Files\dotnet\dotnet.exe"`; the C# app builds with
`dotnet build VoiceRemote/VoiceRemote.csproj -c Release` from the sibling repo
root — that repo has its own conventions; read its code before writing any.

## Architecture invariants (enforced in review)

- `matter/` ↛ `ipc/` (and vice versa); they meet in `index.ts` via `mapping/`.
- matter.js types stay behind `matter/adapter.ts`; protocol types behind
  `ipc/protocol.ts`; both boundaries are the ONLY places their packages are
  imported.
- All inbound IPC frames zod-parsed; all trust boundaries validated.
- Pure logic (`mapping/`, protocol schemas) has ≥ 90 % test coverage.
- IPC binds to 127.0.0.1 only; token via env; token never logged/persisted.

## Integrator workflow (main session)

1. Pick ready stories from BACKLOG (deps done, files disjoint) → delegate in
   parallel with explicit file ownership + story text + this file's rules.
2. On reports: verify evidence, run `npm run verify` fresh, adversarial-review
   sprint tails (S*-R stories) with a different model than wrote the code.
3. Merge = conventional commit per story on `main`; update BACKLOG story
   status; keep README "Status" honest.
4. Hardware-in-the-loop stories (S0-3/S0-4, E2E) require the human for phone /
   Google Home app steps — schedule them explicitly, never fake the evidence.
