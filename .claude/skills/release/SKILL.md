---
name: release
description: Performs MatterHelm's consequential release routine from independent review through tag, workflow monitoring, and asset verification. Use it only when a maintainer explicitly requests a release.
when_to_use: Use for an explicitly authorized MatterHelm version release.
argument-hint: "[version]"
disable-model-invocation: true
---

# Release MatterHelm

Interpret `[version]` as `X.Y.Z`. This procedure changes versions, commits, tags, pushes, and
publishes artifacts; stop before any step not explicitly authorized by the maintainer.

1. Confirm the worktree and intended change set. Obtain an independent pre-release review by
   a different model family from the author; resolve every blocker and justified should-fix.
2. Confirm `.node-version` and `global.json` are on the latest security patches using the
   official feeds: <https://nodejs.org/dist/index.json> and
   <https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json>.
3. Run the `merge-gates` skill in full, including relevant demos, protocol parity, audit, and
   packaging smoke. A release does not inherit earlier gate results.
4. Set `X.Y.Z` in `app/MatterHelm/MatterHelm.csproj`, `bridge/package.json`, and
   `bridge/package-lock.json`. Confirm all three values match.
5. Rename CHANGELOG `[Unreleased]` to `[X.Y.Z] - YYYY-MM-DD`, add a new empty `[Unreleased]`
   section, and update comparison links if present. Keep README evidence generic rather than
   freezing test counts or transient results.
6. Review the final change set and rerun any check affected by release-file edits. Before
   tagging, run `gh workflow run release.yml --ref release/X.Y.Z`, download its 14-day
   unattested candidate artifact, and complete the hardware release-candidate checks.
7. Create one conventional release commit, for example `chore(release): vX.Y.Z`.
8. Create annotated tag `vX.Y.Z`, then push `main` and that tag.
9. Watch both release and CI workflows to completion with `gh run list` and `gh run view`.
   After the draft-first run succeeds, the workflow publishes the release.
10. Verify the published release contains exactly the expected named assets:
   `MatterHelm-Setup-X.Y.Z.exe`, `matterhelm-vX.Y.Z-win-x64.zip`, and `SHA256SUMS.txt`.
11. Download or otherwise read `SHA256SUMS.txt`, compute SHA-256 for both binaries, and confirm
    each hash and filename match before announcing the release.

A failed release run that reached release creation leaves a draft with assets. Delete it with
`gh release delete vX.Y.Z --yes` (this does not delete the tag) before re-running or re-tagging,
so the stale draft can never be published. Then diagnose and fix the cause; if nothing was
published, delete the local and remote failed tag and recreate it on the corrected release
commit. Re-tagging is preferable to burning a version when nothing was published. If anything
was published, stop and ask the maintainer for the recovery decision.
