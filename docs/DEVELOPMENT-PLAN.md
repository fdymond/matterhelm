# Development plan

Agile delivery plan tuned for **AI sub-agent orchestration**: small,
independently verifiable stories; explicit contracts between parallel work;
evidence-based acceptance. Process weight is deliberately minimal — everything
here exists to raise code quality, not to perform ceremony.

> The sprint descriptions are a historical delivery plan, not the current
> product specification. BLUEPRINT §§2.2–2.3, accepted ADRs, and the user guide
> define shipped behavior. Delivery continued through Sprint 11; BACKLOG is
> the live work index. Later work replaced the early momentary switch model
> with ADR-012 retained/resettable semantics.

## Method

- **Iterations**: the initial plan used four short sprints (0–3), each ending
  in something demonstrable. Later shipped sprints through Sprint 11 are
  indexed in BACKLOG. Sprint scope is fixed by the backlog; discoveries create
  new stories, they do not silently expand old ones.
- **Trunk-based**: `main` is always green. Work lands as small, reviewed
  increments (agent worktrees → integrator merges). Conventional Commits
  (`feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `chore:`).
- **Definition of Ready** (story may be delegated): acceptance criteria are
  testable, file ownership is disjoint from concurrently running stories,
  dependencies listed in BACKLOG are done.
- **Definition of Done** (story may be merged):
  1. Acceptance criteria demonstrated with **command output or test results in
     the report** — claims without evidence are not done.
  2. `npm run verify`, the Release app build, and the relevant app tests are
     green with **zero warnings**.
  3. New/changed protocol or design decisions recorded (protocol.ts comments,
     or an ADR for architecture changes).
  4. No TODOs without a linked backlog story; no dead code; docs touched if
     behavior changed.
- **Reviews**: every implementation story gets an adversarial review pass by a
  second agent (different model preferred) hunting for correctness, races,
  protocol drift, and bloat — findings verified before fixing (no
  plausible-but-wrong churn).
- **ADRs**: any deviation from BLUEPRINT.md needs a one-page ADR in `docs/adr/`
  (`NNN-title.md`: context, decision, consequences). The blueprint stays the
  single source of truth.
- **Spikes before product code**: the two riskiest initial assumptions (hub
  requirement, momentary UX) were validated on real hardware in Sprint 0. A
  failed spike re-plans the project *cheaply* instead of sinking a build.

## Quality gates (enforced in CI)

v0.7.1 tag: 393 bridge / 826 app; current counts are published by CI. Both C#
projects target
`net10.0-windows10.0.17763.0`; app line coverage is about 58 %, with a 45 % CI
threshold.

| Gate | Tool | Bar |
|---|---|---|
| Types | `tsc --noEmit` strict | 0 errors |
| Lint/format | ESLint (+ Prettier check) | 0 warnings |
| Unit tests | Vitest | green; `mapping/` + `ipc/protocol` ≥ 90 % line coverage (pure logic — no excuse) |
| Integration | Vitest + mock WS peer | protocol round-trips, reconnect, auth-reject |
| App build/tests | .NET 10 Release build + test | 0 warnings; all tests green |
| App coverage | Coverlet/ReportGenerator | ≥ 45 % line coverage in CI |
| Perf budget | manual per release | ADR-007/G6: idle CPU < 0.5 % both; tray private ≤ 32 MB; one sidecar process private ≤ 120 MB; cold start < 3 s |
| E2E | manual, scripted checklist | pairing + each voice action on real Google Home hardware, results logged in `docs/e2e-log.md` |

CI: GitHub Actions on push/PR run `npm ci` and `npm run verify` for the bridge
on `windows-latest` and `ubuntu-latest` (protocol/mapping logic is OS-neutral;
Windows catches platform drift). The Windows app job builds
`app/MatterHelm/MatterHelm.csproj`, tests
`app/MatterHelm.Tests/MatterHelm.Tests.csproj`, and enforces coverage, all in
Release configuration.

## Sprints

### Sprint 0 — foundations & de-risking
Scaffold verified (deps installed & pinned, verify pipeline green), CI up, and
the two hardware spikes answered. **Exit demo**: a minimal OnOff virtual device
paired with the real Google Home; documented answer to the hub question.

### Sprint 1 — the bridge, properly
Initial device model (Speaker + then-momentary switches + power), IPC client with auth
+ reconnect, pure mapping layer, persisted commissioning. All logic unit-tested
against a **mock tray-app** WS peer. **Exit demo**: "Hey Google, set HTPC
volume to 40 %" reaches the mock peer as `{"type":"action","name":"setVolume","value":40}`.

### Sprint 2 — the tray application (C#, in `app/`)
The standalone product shell: sidecar supervisor (spawn/token/backoff/stdin
tether), loopback WS server, `ActionExecutor` (current-session SMTC absolute
Play/Pause with safe logged failure when unavailable; Windows media controls for next/previous,
CoreAudio volume/mute with change observation, display power), click-through
**overlay HUD** (incoming command + executed action, update-in-place flash),
pairing-QR window, tray states/menu, config + rolling log. **Exit demo**:
end-to-end "Hey Google…" → sidecar → executor → media actually pauses; the
overlay flashes the command; the Home-app volume slider tracks local changes.

### Sprint 3 — hardening & ship
Node SEA packaging + `dotnet publish` self-contained app, one `build.ps1`
producing a single dist folder, crash/restart chaos pass, unpair/factory-reset
flow, user guide, version 0.1.0. **Exit demo**: clean machine → copy dist →
choose **Pair with Google Home…** (which starts the unpaired bridge) → pair →
control, no dev tools involved.

The story-level backlog with sizes, dependencies, and suggested agent/model per
story lives in [`../BACKLOG.md`](../BACKLOG.md).
