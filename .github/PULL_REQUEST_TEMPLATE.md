## Summary

<!-- What does this change, and why? Link the backlog story ID (e.g. S7-3) if applicable. -->

## Definition of Done checklist

Per `docs/DEVELOPMENT-PLAN.md` — a PR isn't done until all of these are true,
with evidence pasted below, not just checked off:

- [ ] `npm run verify` (bridge) and/or `dotnet build -c Release` +
      `dotnet test` (app) are green, **zero warnings**, output pasted below.
- [ ] New/changed pure logic (`bridge/src/mapping/`, `ipc/protocol.ts`) keeps
      ≥ 90% line coverage, or N/A.
- [ ] Protocol parity respected: if `bridge/src/ipc/protocol.ts` changed,
      `app/MatterHelm/Sidecar/Protocol.cs` was updated to match exactly (and
      vice versa) — or N/A. Breaking changes bump the `hello` protocol
      version on both sides and come with an ADR.
- [ ] Relevant demo(s) re-run and passing (exit 0), if this touches UI,
      sidecar↔executor wiring, or config/protocol plumbing — or N/A.
      (`--demo-wired`, `--demo-overlay`, `--demo-pairing-window`,
      `--demo-settings-window`, `--demo-sidecar-chaos`)
- [ ] Docs updated if behavior changed (README status, `docs/BLUEPRINT.md`,
      `BACKLOG.md` story status) — or N/A.
- [ ] New architectural decision or deviation from `docs/BLUEPRINT.md` has an
      ADR in `docs/adr/` — or N/A.
- [ ] No TODOs without a linked backlog story; no dead code left behind.

## Evidence

<!-- Paste verbatim command output (verify/test/demo runs). "Should work" is not evidence. -->

```
$ npm run verify
...
```

## File ownership / scope

<!-- Which files/directories does this PR touch? Confirm it matches the assigned story's scope (CLAUDE.md ground rule 2). -->
