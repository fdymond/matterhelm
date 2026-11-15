---
paths:
  - ".github/**"
---

# GitHub workflow rules

- Pin every action to a full 40-hex commit SHA and retain a version comment so supply-chain
  review is reproducible and updates remain understandable.
- Grant least privilege: keep top-level permissions minimal and add write permissions only to
  the job that needs them.
- Preserve release asset names exactly because the in-app updater consumes them:
  `MatterHelm-Setup-<ver>.exe`, `matterhelm-v<ver>-win-x64.zip`, and `SHA256SUMS.txt`.
- Require the release tag version to match both the app csproj and `bridge/package.json`.
- Do not cancel an in-progress tag build; release artifacts must finish as one coherent set.
