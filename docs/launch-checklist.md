# Public-launch checklist

Gate list for taking the repository public and announcing it. Ordered:
blockers first, then strongly-recommended, then post-launch. Compiled
2026-08-23 against current community-standards practice (GitHub community
profile items; the distribution patterns of the most-adopted Windows OSS
apps — PowerToys, ShareX, Files — GitHub Releases + installer + portable +
checksums, winget as the developer-facing channel).

## Blockers — do not flip the repo public before these

- [ ] **CSA trademark review (NOTICE).** The tray icon *is* the Matter
  certification mark, and the product name contains "Matter". The
  certification mark is generally restricted to certified products.
  Resolution options, in order of safety: (a) replace the icon with an
  original glyph and rename, (b) keep the name but replace the icon and
  add prominent non-affiliation language (already in README/NOTICE), or
  (c) request permission from the CSA. **A human/legal decision — not
  resolvable in code.**
- [ ] **Repo history scan.** Run a secret scanner (e.g. `gitleaks`) over the
  full history before flipping public — the history ships with the repo.
  Nothing sensitive is *known* to be committed (tokens are env-only by
  design), but verify, don't assume.
- [ ] **Hardware E2E pass** (`docs/e2e-log.md`) — the checklist should be
  green on the exact release build the announcement points to.

## Strongly recommended before announcing

- [ ] Enable **GitHub features**: Issues (forms already in place),
  Discussions (Q&A + Show-and-tell), Dependabot alerts + security updates,
  secret scanning + push protection, branch protection on `main`
  (require CI green; CODEOWNERS review).
- [ ] **Release hygiene** (pipeline already produces): installer + portable
  zip + `SHA256SUMS.txt` on every tag; release notes generated from
  CHANGELOG. Consider code-signing (an OV cert or Azure Trusted Signing)
  to stop the SmartScreen prompt — the single biggest first-run friction.
- [ ] **README screenshots/GIF** — settings window, overlay flash, Home-app
  tiles. The single highest-leverage README improvement not yet done
  (needs curated captures of the real UI).
- [ ] Add repo **topics** (`matter`, `google-home`, `smart-home`, `windows`,
  `tray-application`, `htpc`) and a concise repo description + website
  field (user guide link).

## Post-launch

- [ ] **winget manifest** (`winget-pkgs` PR) once the first public installer
  release exists — the standard developer-facing install channel.
- [ ] Watch items already in BACKLOG: Google test-VID commissioning-policy
  changes, Matter 1.6 DeviceLoadStatus, IPv6 privacy-address spike.
- [ ] Issue triage cadence + `good first issue` labels once traffic exists.

## Already in place (verified 2026-08-23)

MIT LICENSE · NOTICE (trademark + third-party) · README (features, install,
verify, docs map, build-from-source) · CONTRIBUTING · SECURITY (private
advisories) · Contributor Covenant CoC · SUPPORT · issue forms + PR template
· CODEOWNERS · Keep-a-Changelog · CI on 3 jobs with coverage gates ·
tag-triggered release workflow (portable zip + installer + SHA256SUMS) ·
791 automated tests · measured resource budgets (ADR-007) · privacy-scrubbed
diagnostics export · docs with zero references to private/external projects.
