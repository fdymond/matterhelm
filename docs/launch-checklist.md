# Post-launch governance checklist

This checklist records repository governance for the public
<https://github.com/fdymond/matterhelm> project. It complements the release
checklist: v0.7.1 remains the current release, while release-specific evidence
belongs in the release notes and [`e2e-log.md`](e2e-log.md).

## Applied repository governance

- [x] **Public visibility:** the repository is public (verified 2026-11-15).
- [x] **Dependency security:** the dependency graph, Dependabot alerts, and
  Dependabot security updates are enabled (verified 2026-11-15).
- [x] **Secret protection:** secret scanning and push protection are enabled;
  the full, already-scrubbed history reports zero alerts (verified 2026-11-15).
- [x] **Private vulnerability reporting:** GitHub Security Advisories exposes
  **Report a vulnerability** to private reporters (verified 2026-11-15).
- [x] **Pull-request dependency review:** dependency review runs on pull
  requests (verified 2026-11-15).
- [x] **Collaboration settings:** Discussions and automatic deletion of head
  branches after merge are enabled (verified 2026-11-15).
- [x] **Merge policy:** squash and rebase are the only enabled merge methods;
  auto-merge is allowed (verified 2026-11-15).
- [x] **Protected `main`:** the `main-protection` ruleset blocks deletion and
  force-pushes, requires linear history and pull requests with zero required
  approvals, and strictly requires `bridge-verify (ubuntu-latest)`,
  `bridge-verify (windows-latest)`, `app-build`, and `Package`. Repository
  administrators may bypass it for solo trunk work (verified 2026-11-15).

## Open release governance

- [ ] **Immutable releases:** the draft-first workflow is in place; verify the
  first draft-first release (0.7.2), then enable immutable releases.
- [ ] **Code signing — maintainer:** complete the SignPath Foundation
  application and, if accepted, integrate Authenticode signing into the
  release path. Until then, retain the documented SmartScreen warning.
- [ ] **OpenSSF Best Practices badge — maintainer:** apply, resolve any unmet
  criteria honestly, and add the badge only after the project qualifies.
- [ ] **Exact-release hardware E2E — maintainer/human:** run
  [`e2e-log.md`](e2e-log.md) against the exact candidate installer or portable
  asset on real Google/Nest hardware. Prior hardware exercise is not release
  sign-off.
- [ ] **Screenshots — maintainer/human:** capture current Settings, overlay,
  pairing, and Google Home tile views; scrub personal information before
  publication.
- [ ] **CSA name/trademark review — maintainer/legal:** review the project
  name, non-affiliation language, original helm icon, and `NOTICE` against
  current CSA guidance.

## Ongoing maintenance

- [ ] Before each release, check the official Node 22 and .NET 10 feeds for
  security releases and bump `.node-version` / `global.json` as needed.
  Dependabot covers `global.json` but cannot update `.node-version`.
- [ ] Review dependency, code-scanning, secret-scanning, and Scorecard alerts
  during routine maintenance and before each release.
- [ ] Verify public README badges, issue templates, private vulnerability
  reporting, release downloads, and documentation links after workflow or
  repository-setting changes.
- [ ] Monitor Google test-VID policy, Matter/matter.js changes, controller
  reconnect behavior, and upstream resolution of ADR-010.
