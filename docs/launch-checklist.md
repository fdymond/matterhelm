# Public-launch checklist — 0.5.0

Do not tag, publish, or announce 0.5.0 until every blocker is checked with
evidence from the exact release candidate. Historical 0.3/0.4 evidence does
not satisfy a 0.5.0 row.

## Release blockers

- [ ] **Documentation/version gate:** README, `[Unreleased]` changelog,
  user/distribution guides, BLUEPRINT §§2.2–2.3, ADR status/supersession,
  backlog, E2E and this checklist agree with the final code. Set version/date
  only at tag time.
- [ ] **Windows floor re-check:** copy the final `TargetFramework` values from
  both C# projects into README/user/distribution docs and record them here.
  Documentation pass value: `net10.0-windows10.0.17763.0`; concurrent source
  work means the integrator must re-check it.
- [ ] **Bridge merge gate:** run `cd bridge; npm.cmd run verify`; expected
  inventory is 391 tests. Documentation sandbox evidence is alternate runner
  391/391, but standard Vitest startup hit `spawn EPERM`; paste a clean standard
  summary here: **PLACEHOLDER**.
- [ ] **App merge gate:** run
  `& 'C:\Program Files\dotnet\dotnet.exe' test app/MatterHelm.Tests/MatterHelm.Tests.csproj -c Release`;
  expected inventory is 720 tests. Documentation sandbox result was 674 pass /
  46 fail from invalid `HttpListener` handles and downstream timeouts; paste a
  clean normal-environment summary here: **PLACEHOLDER**.
- [ ] **Protocol parity:** verify exact-v5 frame union/fields in
  `bridge/src/ipc/protocol.ts`, `app/MatterHelm/Sidecar/Protocol.cs`, and
  BLUEPRINT §2.3; focused parity tests green.
- [ ] **Exact artifacts:** build and retain names/hashes for
  `MatterHelm-Setup-<version>.exe`,
  `matterhelm-v<version>-win-x64.zip`, and `SHA256SUMS.txt`. Confirm the zip
  includes app files, sidecar, `LICENSE`, `NOTICE`, and `README-dist.md`.
- [ ] **0.4.x upgrade:** run `docs/e2e-log.md` against an existing installed
  copy with old custom commands. Confirm config/fabric retention, default
  retained/both-edge migration, reset opt-in, new Play/Pause and Power
  meanings, and protocol-v4 matching-sidecar startup.
- [ ] **Fresh first run/pairing:** unpaired menu shows **Pair with Google
  Home…** (starts/persists bridge) and hides **Enable bridge**. Validate
  gray/amber/blue/green/red states and fresh pairing on real Google/Nest
  hardware.
- [ ] **0.5.0 behavior hardware pass:** complete every non-destructive row in
  `docs/e2e-log.md`, including retained switches, SMTC success/safe failure,
  custom reset, all Power modes, DDC mixed-monitor policy, mDNS selection and
  factory-reset auto-re-pair flow.
- [ ] **Updater:** exercise installed and portable updates with the exact
  assets; hash mismatch must fail safely. Confirm daily/manual checks remain
  consent-gated.
- [ ] **Clean-machine smoke:** installer and portable run without preinstalled
  Node/.NET and without admin rights; firewall guidance is accurate.
- [ ] **Security/release hygiene:** `npm audit`, dependency/license review,
  secret scan of current full history, and privacy-scrubbed diagnostics export
  pass on the final tree.
- [ ] **Trademark/name review:** human/legal confirmation that MatterHelm name,
  NOTICE, README non-affiliation wording, and original helm icon are acceptable.

## Strongly recommended before announcement

- [ ] Capture current Settings, overlay, pairing and Home-tile screenshots.
- [ ] Enable Discussions, Dependabot/security alerts, secret scanning/push
  protection, and `main` branch protection as available.
- [ ] Record installer/portable sizes and rerun ADR-007 CPU/private-memory/cold
  start budgets on the release artifacts.
- [ ] Decide code-signing path or explicitly accept the documented SmartScreen
  warning for this release.

## Already present (must still be spot-checked)

- [x] MIT `LICENSE`, trademark/third-party `NOTICE`, `CONTRIBUTING`, `SECURITY`,
  Contributor Covenant, `SUPPORT`, issue forms, PR template, and CODEOWNERS.
- [x] CI/release workflows, installer + portable packaging, checksums,
  Dependabot, measured resource budgets, and local privacy-scrubbed diagnostics.
- [x] Original helm tray icon and non-affiliation language.

## Post-launch

- [ ] Submit a winget manifest after the first public installer release.
- [ ] Monitor Google test-VID policy, Matter/matter.js changes, controller
  reconnect behavior, and upstream resolution of ADR-010.
- [ ] Establish issue triage cadence and label approachable issues.
