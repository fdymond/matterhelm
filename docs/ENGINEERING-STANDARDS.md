# Engineering standards

The bar: **lean, optimized, intuitive, well-structured**. Every rule below
serves one of those four words; anything that doesn't is ceremony and gets cut.

## Principles

1. **Small surface, sharp boundaries.** Three layers (`matter/`, `mapping/`,
   `ipc/`) meeting only in the composition root. matter.js types never leak
   past `matter/adapter.ts`; protocol types never leak past `ipc/protocol.ts`.
   Dependency direction is one-way, enforced by ESLint `import` rules.
2. **Pure core, thin shell.** Everything decidable without I/O (cluster-write →
   action mapping, state → attribute mapping, config validation) is a pure
   function with exhaustive unit tests. I/O modules stay thin enough to be
   obviously correct.
3. **YAGNI, aggressively.** No feature, abstraction, option, or dependency
   without a story that needs it *now*. Deleting code is a valued contribution.
4. **Fail loud locally, degrade gracefully at boundaries.** Internal invariant
   violations throw; boundary failures (socket down, Matter write while
   disconnected) log WARN once and degrade without crashing the bridge.
5. **Evidence over confidence.** A change is done when its test/demo output is
   in the story report — not when it "should work".

## TypeScript (sidecar)

- Node 22 LTS, ES modules, `tsconfig` strict family all on
  (`strict`, `noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`,
  `noFallthroughCasesInSwitch`). **`any` is banned** (`unknown` + narrowing);
  `as` casts need a comment justifying them.
- Runtime validation at every trust boundary with `zod` (config file, every
  inbound IPC frame). Parse, don't validate-and-hope.
- ESLint (typescript-eslint strict + import plugin) + Prettier. Zero warnings
  is the merge bar — a warning is a decision postponed.
- Naming: files kebab-case; no `utils.ts` grab-bags; a module's name states
  what it owns. Functions ≤ ~40 lines or they're hiding a concept.
- Comments explain **why**, mirror-of-code comments are deleted. Public module
  APIs get JSDoc one-liners.
- Logging: pino, structured, one line per event
  (`{evt: "action", name, ok}`), silent when idle. Log level via config.
- Dependencies: production deps require justification in the PR/story report;
  pin exact versions; `npm audit` clean at release.
- Errors: never swallow; `catch` blocks either handle meaningfully or add
  context and rethrow. No empty catches.

## C# (tray application, `app/`)

House style for `HtpcMatterBridge` (written fresh here — no code imported from
other repos): XML doc `<summary>` on public members, terse why-comments,
`_camelCase` fields, locks with documented discipline, events marshalled to the
UI thread via `SynchronizationContext` and fired outside locks, P/Invoke over
new dependencies (a NuGet needs justification). Build with
**warnings-as-errors**; WinForms is not trim-compatible — never add
`PublishTrimmed`. UI never blocks on IPC or process supervision; the overlay
HUD is click-through, non-activating, and updates in place.

## Testing

- **Pyramid**: many unit tests on `mapping/` + `protocol` (≥ 90 % lines — pure
  logic has no coverage excuse); integration tests with a mock WS peer
  (auth-reject, reconnect/backoff, momentary reset timing via fake timers);
  scripted manual E2E on real Google Home hardware, logged in `docs/e2e-log.md`.
- Tests are specification: name them for behavior
  (`"volume write of 254 maps to setVolume 100"`), not for methods.
- Fake timers for all time-dependent logic; no sleeps in tests; suite < 30 s.

## Git & releases

- Conventional Commits; imperative subject ≤ 72 chars; body = why.
- Small merges: one story = one merge to `main`; `main` always green.
- SemVer from 0.1.0; protocol version is independent (`protocol.ts`) and only
  ever bumped with an ADR.
- No secrets in the repo, ever. The IPC token is generated at runtime by the
  supervisor and passed via environment — never logged, never persisted.

## Security

- WS server/client bound to `127.0.0.1` only; token-authenticated hello; close
  on first invalid frame. zod-validate every field of every inbound frame.
- Matter storage dir contains fabric credentials — user-profile scoped
  (`%APPDATA%`), never in the repo or dist archives.
- The sidecar executes **no** shell commands and takes **no** network input
  except Matter (matter.js) and the loopback IPC.
