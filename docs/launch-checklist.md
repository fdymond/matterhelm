# Public-launch checklist

This checklist governs the switch of
<https://github.com/fdymond/matterhelm> from private to public. It is not a
version-bump or release checklist: v0.7.1 remains the current release. Do not
flip repository visibility until each unchecked launch-decision row is
completed or explicitly waived by the maintainer.

## Verified preparation

- [x] **Clean automated baseline:** v0.7.1's clean suites were green; current
  counts are published by CI rather than frozen in this preparation document.
  App line coverage was about 58 %, above CI's 45 % threshold
  (verified 2026-09-10).
- [x] **Windows floor:** both C# projects target
  `net10.0-windows10.0.17763.0`, and the installer minimum is Windows build
  17763: Windows 10 version 1809+ x64.
- [x] **Protocol parity:** `bridge/src/ipc/protocol.ts` and
  `app/MatterHelm/Sidecar/Protocol.cs` both declare IPC message revision 5.
- [x] **Published v0.7.1 artifacts:** the release includes
  `MatterHelm-Setup-0.7.1.exe`, `matterhelm-v0.7.1-win-x64.zip`, and
  `SHA256SUMS.txt`.
- [x] **Distribution contents and notices:** `build.ps1` copies `LICENSE`,
  `NOTICE`, and `docs/README-dist.md`, generates
  `THIRD-PARTY-NOTICES.txt` for all bundled npm production packages, Node.js,
  and QRCoder, and writes `sidecar-layout.json`. The release workflow zips that
  folder and builds the Inno Setup installer.
- [x] **Dependency audit:** `npm audit` reports 0 vulnerabilities after Vitest
  4.1.11 removed the GHSA-82fw-gwwq-j7x9 findings; no vulnerable NuGet packages
  were reported in the verified 2026-09-10 audit.
- [x] **History and secret review:** the full-history scan found no tokens,
  keys, pairing codes, or hostnames. Three commits contain a personal Windows
  path and six contain RFC1918 test addresses; these are non-secret historical
  context, so the decision is no history rewrite. The live S0-3 spike document
  now uses portable paths.
- [x] **Diagnostics privacy:** the export stays local and sanitises
  `config.json` by default (identity seed, launch arguments, and profile paths
  redacted). Its manifest omits identity/paths, and every bundled log/metrics
  line redacts commissioning credentials, machine/user names, profile paths,
  and IPv4/IPv6 literals. Raw config needs a code-only opt-in with no UI.
- [x] **Loopback admission:** `IpcServer` verifies the request remote endpoint
  and returns HTTP 403 for non-loopback callers before the single-client slot;
  its one WARN records the address family only. The Node peer closes the first
  invalid inbound frame with policy code 1008 and reconnects with bounded
  backoff.
- [x] **CI hardening:** GitHub Actions are SHA-pinned and least-privilege;
  CodeQL covers JavaScript/TypeScript and C#, with dependency review, Scorecard,
  and release provenance attestations in dedicated workflows.
- [x] **Release integrity:** v0.7.1 has SHA-256 checksums, and the updater
  verifies the selected package and rechecks its hash before applying it. The
  release workflow now attests build provenance for the next release onward.

## Launch decisions and integrated checks

- [x] **Integrated documentation pass:** README, user/distribution guides,
  BLUEPRINT, amended/new ADRs, CHANGELOG, BACKLOG, E2E log, and this checklist
  describe the public-readiness behavior. A link check covers every file
  changed by the pass.
- [ ] **Commit launch files:** Commit the untracked files (three new workflows,
  `AGENTS.md`, ADR-014, new source/test files) — the CodeQL badge only resolves
  once `codeql.yml` is on `main`.
- [ ] **Trademark/name review (human/legal):** resolve the open `NOTICE`
  “Name” item against the CSA brand guidelines and confirm the project name,
  non-affiliation language, and original helm icon are acceptable.
- [ ] **Exact-release hardware E2E (human):** run the current
  [`e2e-log.md`](e2e-log.md) checklist using the exact v0.7.1 installer or
  portable asset and real Google/Nest hardware. Prior real-hardware exercise
  is useful evidence, but does not replace this launch sign-off.
- [ ] **Screenshots (human):** capture current Settings, overlay, pairing, and
  Google Home tile views, scrub personal information, and decide where they
  belong. No screenshots are currently present, so README must not link to
  any until this is complete.
- [ ] **Code-signing decision (human):** either arrange Authenticode signing
  for future binaries or explicitly accept and retain the documented
  SmartScreen warning for unsigned builds.

## Post-flip settings

- [ ] Add branch protection or a repository ruleset for `main` that requires
  pull requests and the relevant `ci` checks before merge.
- [ ] Enable Dependabot alerts and Dependabot security updates.
- [ ] Enable secret scanning and push protection.
- [ ] Enable code scanning and confirm the CodeQL workflow reports results.
- [ ] Confirm the dependency-review workflow now runs on pull requests after
  the repository is public and fails only high-severity changes in runtime
  dependency scope.
- [ ] Enable private vulnerability reporting so the SECURITY/SUPPORT advisory
  link is available to unauthenticated reporters.
- [ ] Decide whether to enable Discussions; it is optional while the issue
  tracker remains the primary support channel.
- [ ] Enable automatic deletion of head branches after merge.
- [ ] Prefer squash merging so one focused issue/story lands as one
  Conventional Commit on `main`.
- [ ] Retire the user-guide note that anonymous release checks are unavailable
  while the repo is private (user-guide → *Updating*).

## After launch

- [ ] Verify the README badges, issue chooser, private vulnerability-reporting
  route, release downloads, and documentation links as an unauthenticated
  visitor.
- [ ] Establish a sustainable issue-triage cadence for the solo maintainer.
- [ ] Monitor Google test-VID policy, Matter/matter.js changes, controller
  reconnect behavior, and upstream resolution of ADR-010.
