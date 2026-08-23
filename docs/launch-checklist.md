# Public-launch checklist

Gate list for taking the repository public and announcing it. Ordered:
blockers first, then strongly-recommended, then post-launch. Compiled
2026-08-23 against current community-standards practice (GitHub community
profile items; the distribution patterns of the most-adopted Windows OSS
apps — PowerToys, ShareX, Files — GitHub Releases + installer + portable +
checksums, winget as the developer-facing channel).

## Blockers — do not flip the repo public before these

- [x] **Icon half of the CSA trademark review — resolved 2026-08-23**: the
  tray icon is now an original ship's-helm glyph (owner picked candidate A;
  the certification-mark drawing is archived, unshipped, in
  `Ui/TrayIcons.cs`). Remaining:
- [ ] **Name half of the CSA review (NOTICE).** "MatterHelm" contains
  "Matter", used descriptively for protocol compatibility (as matter.js,
  python-matter-server, HA Matter Hub do). Non-affiliation language is in
  README/NOTICE. Give it a final look against the CSA brand guidelines —
  a human/legal call, though the descriptive-use position is the ecosystem
  norm.
- [x] **Repo history scan — clean 2026-08-23.** `gitleaks detect` over the
  full history: **89 commits, ~3.03 MB scanned, no leaks found.** (Re-run
  immediately before flipping public if more history has landed by then.)
- [ ] **Hardware E2E pass** (`docs/e2e-log.md`) — needs the human (phone +
  Google Home app + Nest hub). The checklist is prepared and pointed at the
  **v0.3.0 release build** (the one running on the HTPC), with sections
  added for everything shipped since it was written: distribution
  (installer + portable + checksums), macros/system commands, overlay
  theme/opacity, repeat-command behaviour.

## Strongly recommended before announcing

- [x] **Dependabot** — `.github/dependabot.yml` keeps npm, NuGet, and GitHub
  Actions current with grouped weekly PRs (works while private; the alert
  *feed* needs the repo setting enabled in Settings → Advanced Security).
- [ ] Enable the rest in **repo settings** (these are UI toggles, not files;
  several require the repo to be public or GitHub Advanced Security):
  Discussions (Q&A + Show-and-tell), Dependabot alerts + security updates,
  secret scanning + push protection, branch protection on `main`
  (require CI green; CODEOWNERS review).
- [x] **Release hygiene — proven by v0.3.0 (2026-08-23)**: the tag published
  `MatterHelm-Setup-0.3.0.exe` + `matterhelm-v0.3.0-win-x64.zip` +
  `SHA256SUMS.txt` with generated notes. Remaining, and the single biggest
  first-run friction: **code-signing** (an OV cert or Azure Trusted
  Signing) to stop the SmartScreen prompt - a purchase decision, not a
  code change.
- [ ] **README screenshots/GIF** — settings window, overlay flash, Home-app
  tiles. The single highest-leverage README improvement not yet done
  (needs curated captures of the real UI).
- [x] **Repo topics + description — set 2026-08-23** (9 topics: matter,
  matter-protocol, google-home, home-automation, smart-home, windows,
  tray-application, htpc, dotnet). Still to set when public: the website
  field (user-guide link).

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
