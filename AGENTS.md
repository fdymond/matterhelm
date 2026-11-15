# MatterHelm agent playbook

MatterHelm is a standalone Windows tray app and bundled Matter sidecar that expose an
HTPC to Google Home and execute commands locally, without another product or service.

## Read before changing code

1. `docs/BLUEPRINT.md` is the design authority.
2. `docs/ENGINEERING-STANDARDS.md` sets the quality bar.
3. `BACKLOG.md` is the work queue and supplies each story's acceptance criteria.
4. `docs/DEVELOPMENT-PLAN.md` defines Done.

## Worker ground rules

- Take one story per agent and treat its acceptance criteria as the contract; report
  out-of-scope discoveries instead of implementing them.
- Edit only explicitly assigned files, because parallel workers share one working tree.
- Report gate output verbatim as evidence; summaries cannot establish the Definition of Done.
- Leave state-changing Git operations (`commit`, `checkout`, `reset`, `stash`, tags, and
  pushes) to the integrator, because workers share one working tree. Read-only Git is fine.
- Do not touch `%APPDATA%` user state. Use temporary state in tests and demos so a worker
  cannot damage a maintainer's live Matter fabric or settings.
- Keep the product fully standalone per ADR-001; do not reference or depend on code,
  configuration, or services outside this repository.
- Record a blueprint deviation in an ADR rather than silently changing the architecture.
- Produce zero warnings, follow the surrounding style, and delete obsolete code rather
  than commenting it out so the maintained path stays unambiguous.
- Hardware-in-the-loop work requires a human for phone, Google Home, and physical-device
  steps; report it as unverified until performed and never invent evidence.

## Merge gates

Run the full merge gates and relevant demos through the `merge-gates` skill; see that skill
and `CONTRIBUTING.md` for the demo harnesses and commands.

## Commands

Use the exact Node.js version in `.node-version` (`build.ps1` refuses any other; nvm/fnm/Volta
users can read the file) and the .NET SDK selected by `global.json`, including its allowed
patch roll-forward.

## Architecture invariants

- Keep `matter/` and `ipc/` independent and compose them through `mapping/` in `index.ts` so transport and Matter behavior remain separable.
- Keep matter.js behind `matter/adapter.ts` and wire types behind `ipc/protocol.ts` so external churn has one boundary.
- Parse inbound IPC frames at their boundary so internal code receives only validated data.
- Keep pure logic at least 90% covered so mapping and protocol regressions fail the gates.
- Keep IPC loopback-only (the tray rejects non-loopback remote endpoints before admitting a client), pass the session token only by environment and never log or persist it, and close on the first invalid frame, so local untrusted input cannot cross the trust boundary.
- Keep `BridgeHost` as the facade over its lifecycle, dispatch, and state collaborators so concurrency ownership stays explicit.
- Keep `MediaKeys` as the facade over focused routing and Windows delivery so policy stays separate from OS calls.
- Treat BLUEPRINT sections 2.2 and 2.3 as normative so device behavior and IPC remain consistent.
- Keep the C# and TypeScript protocols exactly aligned and use an ADR plus a `hello.protocol` bump for breaking changes so compatibility is explicit.
- Use the `matterjs-upgrade` skill for every matter.js upgrade so ADR-010 and device behavior are empirically re-verified.

Do not record release versions, test counts, or other fast-aging state here. Use README,
CHANGELOG, and CI for current status.
