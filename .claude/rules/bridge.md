---
paths:
  - "bridge/**"
---

# Bridge rules

- Use the TypeScript strict configuration and strict typed ESLint family; keep the bridge
  free of type and lint warnings.
- Do not use `any`. Use `unknown` and narrow it so untrusted values cannot bypass checking.
- Add a nearby comment that justifies every `as` cast and explains why runtime evidence makes
  it safe.
- Validate every trust boundary with zod before data enters bridge logic.
- Preserve the ESLint import boundaries: `matter/` and `ipc/` do not import each other,
  matter.js imports live only in `matter/adapter.ts`, and `ws` imports live only in
  `ipc/client.ts` (tests may use `ws` as fixtures).
- Maintain the configured 90% per-file line thresholds for pure logic in `mapping/`,
  `ipc/protocol.ts`, `config.ts`, `timing.ts`, and `matter/diagnostics.ts`.
- Run `npm run verify` from `bridge/` and report its output before declaring bridge work done.
- Use the `matterjs-upgrade` skill for every matter.js upgrade and ADR-010 re-verification.
