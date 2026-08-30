# Backlog

Stories sized for single-agent delegation. Rules: a story is delegated only
when its `Deps` are done and no concurrently running story shares its files.
Sizes: S ≤ half day · M ≤ 1 day · L ≤ 2 days. "Agent" is the suggested
executor profile (see CLAUDE.md for orchestration mechanics).

## Sprint 0 — foundations & de-risking

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S0-1 ✅ | Verify & pin the bridge scaffold: in `bridge/`, `npm install` matter.js (`@matter/main`), zod, pino, tsx + the eslint/prettier/vitest toolchain; make `npm run verify` pass on the placeholder; pin exact versions | `npm run verify` output green in report; lockfile committed; versions listed | S | — | impl (Sonnet) |
| S0-2 ✅ | CI: GitHub Actions `verify` (bridge) on windows-latest + ubuntu-latest (+ app Release build on windows-latest, pulled forward from Sprint 2 since S0-5 landed) | Green run demonstrated | S | S0-1 | integrator |
| S0-3 ✅ (pairing + persistence PASS on real hardware; Speaker UX + momentary validation moved to product E2E; hub-reconnect risk logged) | **SPIKE** (re-scoped by ADR-002): with test VID/PID registered in a free Google Home Developer Console project + Nest hub on LAN, commission a minimal matter.js bridge (Speaker + one OnOff endpoint); verify Speaker volume UX (voice % + app slider) | Result documented in `docs/spikes/S0-3-pairing.md` with log evidence: console recipe, hub confirmation, Speaker UX verdict; **needs the human** for console signup + phone/Home-app steps | M | S0-1 | impl (Fable) + human |
| S0-4 ⛔ superseded by ADR-012 | Historical spike proposal: 800-ms default-momentary OnOff UX. The reset experiment is no longer applicable; retained switches and opt-in reset shipped. Only the separate Generic Switch hardware research question remains open. | If pursued, record only Generic Switch Home-app/routine surfacing; do not revive the obsolete reset model | S | S0-3 | human |
| S0-5 ✅ | `app/` scaffold: `HtpcMatterBridge.csproj` (net8.0-windows, WinForms, single-instance Program.cs, Log.cs, empty TrayContext showing an icon), builds with warnings-as-errors | `dotnet build -c Release` 0 warnings; exe shows tray icon; report screenshot/log | S | — | impl (Sonnet) |

## Sprint 1 — the bridge (`bridge/`)

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S1-1 ✅ | `ipc/protocol.ts` + zod schemas + exhaustive unit tests (valid/invalid frames, version field, pairing msg) | ≥ 90 % coverage on the module; tests read as a protocol spec | S | S0-1 | impl (Sonnet) |
| S1-2 ✅ | `mapping/actions.ts` + `mapping/state.ts` pure functions + tests (incl. 0–254 ↔ 0–100 % rounding edges) | ≥ 90 % coverage; edge-case tests | S | S1-1 | impl (Sonnet) |
| S1-3 ✅ (historical model; reset policy superseded by ADR-012) | `matter/adapter.ts` + `bridge.ts` + `devices.ts`: initial Aggregator with Speaker, 3 then-momentary switches, power toggle; persisted storage | Manual: all endpoints visible in the Home app; storage survives restart | L | S0-3, S0-4, S1-2 | impl (Fable) |
| S1-4 ✅ | `ipc/client.ts`: WS client, token hello, jittered-backoff reconnect, graceful degradation when peer absent | Integration tests vs mock server: auth-reject closes, actions drop with one WARN when down, reconnect works (fake timers) | M | S1-1 | impl (Fable) |
| S1-5 ✅ (full bridge ran vs mock tray peer: hello/pairing/state + tether shutdown) | `config.ts` + `index.ts` composition root + pino logging + `pairing` message emission; config incl. `mdnsInterface` (BLUEPRINT §2.1 Windows notes) | `npm start` runs the full bridge against a mock tray-app peer; README quick-start true | S | S1-3, S1-4 | impl (Sonnet) |
| S1-6 ✅ (violations demonstrably fail; resolver dep required and pinned) | ESLint import-boundary enforcement per ENGINEERING-STANDARDS: `matter/` ↛ `ipc/` (and vice versa), `@matter/*` imports only in `matter/adapter.ts`, `ws`/socket types only behind `ipc/` (via `import-x/no-restricted-paths` or `no-restricted-imports`; add `eslint-import-resolver-typescript` only if needed) | Lint demonstrably fails on a violation (show output), then verify green | S | S1-3, S1-4 | impl (Sonnet) |
| S1-R | Adversarial review of Sprint 1 (races, protocol drift, matter.js leakage past adapter, bloat) | Findings verified + fixed or explicitly waived | M | S1-5 | review (different model) |

## Sprint 2 — the tray application (`app/`)

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S2-1 ✅ (pulled forward of S1-5: protocol + env contracts were already fixed; chaos-tested vs stub children) | `Sidecar/`: `SidecarSupervisor` (spawn node/SEA exe, env token, restart backoff, stdin tether, stdout→log) + `IpcServer` (loopback WS, hello/auth, close on invalid frame) + `Protocol.cs` typed records | Protocol unit tests; chaos demo: kill sidecar → auto-restart with backoff logged; wrong token → socket closed | L | S0-5, S1-5 | impl (Fable) |
| S2-2 ✅ (later amended by S10-31) | Initial `ActionExecutor`/media keys/CoreAudio/display power; dedicated Play/Pause now uses SMTC without an intent-inverting appcommand fallback | Manual demo: each action works on real media; volume observation fires state updates | M | S0-5 | impl (Fable) |
| S2-3 ✅ (historical overlay; superseded by S10-19) | Initial click-through HUD; current UI is static MatterHelm header plus one command/result pill or volume bar | Manual demo incl. rapid-fire updates without flicker; never steals focus or blocks clicks | M | S0-5 | impl (Sonnet) |
| S2-4 ✅ (historical tray model; blue added by ADR-011, contextual menu by S10-23) | Pairing window + tray/menu/config foundation | Manual pairing and name checks carried to hardware E2E | M | S2-1 | impl (Sonnet) |
| S2-5 ✅ (mock-sidecar E2E PASS; "Hey Google" hop deferred to hardware E2E) | Wire it: IpcServer actions → executor → overlay flash → ack; state publisher (volume/mute on connect + on change) | **Exit demo evidence**: "Hey Google…" pauses real media; overlay flashes; Home-app slider tracks local volume change | M | S2-1, S2-2, S2-3, S2-4 | impl (Fable) |
| S2-R ✅ (Opus review: 1 confirmed bug + 2 verified risks, all fixed; COM/threading/security clean) | Adversarial review of Sprint 2 (thread marshalling, supervisor races, P/Invoke correctness, protocol drift vs bridge) | Findings verified + fixed | M | S2-5 | review |

## Sprint 3 — hardening & ship

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S3-1 ✅ | Node SEA single-exe (`npm run package`) + `dotnet publish` self-contained app + root `build.ps1` producing one dist folder; ADR if SEA infeasible | Clean-machine run of the packaged dist pairs & controls; sizes reported | L | S2-R | impl (Fable) |
| S3-2 🔜 partial (implementation/docs done; hardware acceptance still open) | Unpair / factory-reset flow + user guide | Execute and log the current scripted hardware E2E checklist; authored-only is not complete | M | S3-1 | human |
| S3-3 ✅ (budget table in CHANGELOG 0.1.0: all G6/ADR-007 budgets met on the packaged dist; audit 0 vulns; tagged v0.1.0) | Perf/budget pass (BLUEPRINT G6) + `npm audit` clean + release 0.1.0 | Budget table meets G6; tagged release | S | S3-2 | impl (Sonnet) |

## Sprint 4 — settings UI, custom commands, modernization (ADR-004/ADR-005)

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S4-0 ✅ | TFM bump to .NET 10 Windows (current exact TFM `net10.0-windows10.0.17763.0` in both projects) + CI dotnet 10.0.x; PerMonitorV2 confirmed | Build+tests green; `--demo-overlay`/`--demo-wired` re-run PASS (exit 0 both) | S | — | integrator |
| S4-1 ✅ | Bridge: `HTPC_BRIDGE_ENDPOINTS` env (ADR-004 shape) → dynamic endpoint construction (disabled built-ins omitted, custom momentary plugs); protocol v2 (`v:2` + `custom` action w/ `key`) in protocol.ts + mapping | verify green; unit tests for endpoint-set derivation + custom action mapping; mock-run shows custom endpoint write → `{name:"custom",key}` frame | M | S4-0 | impl (Fable) |
| S4-2 ✅ | App: Config `commands` schema + migration from `deviceNames`; Protocol.cs v2 parity; executor custom actions (`mediaKey`, `launch`); supervisor env → ENDPOINTS | dotnet tests incl. migration + v2 parity + custom dispatch; wired demo extended with a custom command round-trip | M | S4-0 | impl (Fable) |
| S4-3 ✅ (incl. PairingWindow dark-theming + production SetColorMode by integrator) | App: `Ui/SettingsWindow` per ADR-004 (left nav categories, top search filter, staged edits + validation, custom command CRUD editor, dark mode via SetColorMode) | Build 0 warnings; view-model/filter logic unit-tested; screenshot evidence light+dark; all settings round-trip to config.json | L | S4-2 | impl (Fable) |
| S4-4 ✅ (zero suppressions; behavior-preserving, demos re-run PASS) | Modernization refactor per ADR-005 (both projects): TimeProvider, LibraryImport, STJ source-gen, Lock, collection exprs, async-void audit, AnalysisMode=Recommended triage | verify + dotnet tests green at new analyzer bar; no behavior changes (demos re-run PASS) | L | S4-1, S4-2, S4-3 | impl (Fable) |
| S4-5 ✅ | Owner-reported: DPI overlap fix (PairingWindow rebuilt; all windows audited at 200 %) + overlay volume percentage-bar pill | Screenshots at real 200 % display; pixel-sampled fill assertions; 272 tests + 3 demos green | M | S4-3 | impl (Fable) |
| S4-R ✅ (Opus: 0 confirmed bugs, protocol parity verified empirically; 2 RISKs + NITs fixed by integrator) | Adversarial review of Sprint 4 (protocol v2 parity, settings UX correctness, migration safety, refactor regressions) | Findings verified + fixed | M | S4-4 | review (Opus) |

## Sprint 5 — telemetry, diagnostics, latency instrumentation (owner-directed)

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S5-0 | Research: latest Matter/matter.js expertise (diagnostics APIs, session/subscription tuning, hub re-association) + .NET 10 local-telemetry best practice | Two cited reports; findings folded into ADR-006 (telemetry design) | S | — | research (Sonnet) ×2, in flight |
| S5-1 ✅ (0.17.7 bump; session events unit-proven — live capture needs commissioning) | Bridge diagnostics: matter.js log-level/facility surfacing via env, session/subscription observability events into pino, per-action timing (cluster write → WS send) with correlation ids on frames | verify green; timing lines visible in a mock run | M | S5-0 | impl (Fable) |
| S5-2 ✅ (incl. CopyInto OverlayPosition fix) | App diagnostics: per-action timing (frame recv → executed → acked), Meter counters (actions, acks, restarts, reconnects), runtime app log level in config+settings, "Export diagnostics" (zip logs+versions+env, token-redaction verified by test) | tests incl. redaction; timing visible in wired demo | M | S5-0 | impl (Fable) |
| S5-3 ✅ (45% app gate at measured baseline; bridge per-file 90% proven to fire) | Coverage & validation in CI: C# coverage collection + threshold, bridge coverage job publishing summaries; both surfaced in Actions summary | CI green with coverage tables | S | S5-0 | impl (Sonnet) |
| S5-R ✅ (Opus: 1 HIGH passcode-in-bundle leak + 3 risks, all fixed with tests) | Adversarial review of Sprint 5 (privacy: no token/PII in any diagnostic path; perf overhead of instrumentation) | Findings verified + fixed | S | S5-1..3 | review (Opus) |

## Sprint 6 — deep review & measured optimization (owner-directed)

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S6-R ✅ | Whole-repo deep production-quality review (resource lifecycle, allocations, idle behavior, altitude) | Verdict: production-grade, no bugs/leaks; 4 verified findings + hotspot map | M | — | review (Opus) |
| S6-1 ✅ | Measured resource optimization: esbuild sidecar bundle (1 process, ~90MB private, 0.9s start), WinForms-baseline memory truth, churn probe (`--probe-resources`), metrics idle-churn fix; ADR-007 restates G6 | Before/after tables; probes PASS; all suites green | M | — | impl (Fable) |
| S6-2 ✅ | Review findings applied: Program.cs 916→180 (Demos/ extraction + helper dedup), dead native window removed, TrayContext Dispose(bool) teardown (ghost-icon fix), audio-device-removed WARN | Behavior-preserving: 309 tests unmodified green; all 6 demos identical exit 0 | S | S6-R, S6-1 | impl (Sonnet) |

## Sprint 7 — key actions, fast taps, MatterHelm rename, OSS setup (owner-directed)

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S7-1 ✅ (historical reset default; S8-2 changed it to 0 and ADR-012 limited it to opted-in custom commands) | keySequence custom actions + configurable reset | INPUT-array proof; both-side default pinned at the time; 391+322 tests | M | — | impl (Fable) |
| S7-2 ✅ | Rename product to MatterHelm (namespaces/dirs/mutex/meter/docs/CI) + atomic %APPDATA% migration preserving Matter fabric | Identity test files zero-diff; real migration performed at merge (log line verified, fabric present, old root gone) | M | S7-1 | impl (Fable) |
| S7-3 ✅ (repo renamed to `matterhelm`; npm audit highs fixed; release workflow validates at first v* tag) | OSS-grade project setup: repo → `matterhelm` (private, history kept), LICENSE, CONTRIBUTING, SECURITY, CoC, issue/PR templates, release workflow (changelog+semver tags), branch/PR conventions | Files in place; release workflow dry-run green | M | S7-2 | impl (Sonnet) + integrator |

## Sprint 8 — trigger mechanics (owner-directed)

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S8-1 ✅ | Dispatch plug actions from OnOff **commands** instead of attribute-change events. Reset is not a correctness dependency; ADR-012 later changed endpoint state policy. Speaker remains attribute-driven. | Repeat-command tests green; live-node smoke; no protocol change | M | — | impl (Fable) |
| S8-2 ✅ | Tap reset delay floor 100→0 ms both sides (0 = next-tick reset; safe post-ADR-008), **default 300→0 ms** (owner request), + user-guide note on Google's per-device Type re-typing (plug tile → Switch) | Both range validators, default pins + tests updated (bridge 328, app 411 green); Settings shows "0 = immediately" | S | S8-1 | integrator |
| S8-3 ✅ | Command sequences (macros): `sequence` custom-action type running ordered steps (media key/launch/key chord/`delay` 1–5000 ms; ≤16 steps, delays ≤10 s total, no nesting), step-list editor + SequenceStepDialog, stop-at-first-failure with step-numbered nack | Config parse/round-trip + executor-order + validation tests (app 426 green); app-side only, no protocol change | M | — | integrator |
| S8-6 ✅ | Deep review of Sprint 8 + command-processing hardening: found+fixed macro head-of-line blocking (delays ran on the WebSocket receive loop → froze every later frame and app exit); delay-bearing macros → background runner with cancellable waits (CTS on Dispose), inline ack semantics kept for instant macros | Non-blocking regression test (macro 3 s delay + queued volume frame executes immediately); app 439 + bridge 328 green | S | S8-3 | integrator (review + impl) |
| S8-5 ✅ | `system` custom-action type + macro step (owner request): startScreenSaver (registry .scr /s), stopScreenSaver (nudge), displaysOff/On, sleep, hibernate, lock, closeForegroundProgram (WM_CLOSE), shutdown, restart — executor verbs + SystemCommands.cs natives; combo in both dialogs | Config parse/round-trip + dispatch + choices-complete tests (app 438 green); no protocol change | S | S8-3 | integrator |
| S8-4 ✅ (shared momentary policy superseded by ADR-012) | Hardware established that controller Off is a real user transition. Current mapping keeps both-edge behavior for Next/Previous and retained custom commands, but gives Play/Pause distinct edges and reset-enabled custom commands On-only. | Historical mapping/timing evidence retained | S | S8-1 | integrator |

## Sprint 9 — settings UI polish (owner-directed)

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S9-1 ✅ (media premise corrected by S10-31) | Settings UI polish + Play/Pause-labelled media actions. Hardware later proved `APPCOMMAND_MEDIA_PLAY` toggles; current absolute verbs use SMTC and fail safely when unavailable. | UI demo evidence; media behavior superseded by S10-31 | S | — | integrator |
| S9-2 ✅ | Devices & Commands compaction + scroll polish (owner-directed): CommandRow kind merges name+enabled into one row (leading checkbox, untick greys row + disables name box), filler row removed (scrollbar matched to content), WM_MOUSEWHEEL message filter scrolls the hovered page regardless of focus | Demo screenshots verified (5 compact rows, page nearly fits unscrolled); app 445 green | S | S9-1 | integrator |
| S9-3 ✅ | Nav restructure (owner-directed): "Devices & Commands"→"Devices" with a bold "Google Home devices" SectionHeader + description-free command rows; custom commands→own "Custom devices" section (full-height list); wheel filter fixed by FORWARDING the message to the hovered page (hand-computed AutoScrollPosition silently no-opped) | Screenshots verified both pages; app 445 green | S | S9-2 | integrator |
| S9-4 ✅ | Overlay theming + transparency (owner-directed): OverlayTheme enum (System default/Dark/Light; System resolves AppsUseLightTheme per flash) + light Palette, OverlayOpacityPercent 30-100 scaling every UpdateLayeredWindow push (fade multiplies through); config parse/save/CopyInto/validation, Settings Overlay rows, preview carries staged theme+opacity via OverlayPreviewRequest | 10 new config tests (wire names, digit guard, range fallback, camelCase round-trip); app 455 green; overlay + settings demos PASS | S | S9-1 | integrator |
| S9-5 ✅ | Owner bug reports: (a) staged-theme Preview showed stale panel - ShowContent only re-rendered on content change; now palette-aware (also fixes mid-session system theme flips on identical content); (b) Spotify launch "Access is denied" - MSIX package paths refuse CreateProcess; AppLaunch redirects to the per-user execution alias (actionable error if disabled) + launch nack no longer echoes its success pill | 7 new AppLaunch tests (detection, alias mapping, no-alias failure); app 462 green; overlay demo PASS; Spotify alias verified present on the HTPC | S | S9-4 | integrator |
| S9-6 ✅ | Deep review of Sprint 8-9 surface + resource/memory audit: budgets re-measured (tray 15.4 MB/32, sidecar 93.8 MB/120), churn probe PASS post-S9 (GDI/USER flat over 100 settings cycles + 500 flashes); fixed MED SendMessageW-to-hung-foreground could block the IPC receive loop (appcommands → SendMessageTimeoutW SMTO_ABORTIFHUNG 1 s); added resource gauges to AppMetrics + gauge-aware MetricsFileListener (dedupe on activity only + ≥10 % memory-drift writes, disposed-listener final flush guarded); GH Actions bumped checkout/setup-node v7, setup-dotnet v6 (Node 20 deprecation); npm audit 0 vulns | Probe + wired demo PASS on final build; app 463 green; live snapshot gauge-verified | S | — | integrator (review + impl) |
| S9-7 ✅ | Overlay opacity editor → slider (owner request): SettingKind.Slider (TrackBar + live "NN %" label, 5/10 steps); descriptor unchanged Get/Set so round-trip tests stand | Settings demo PASS, Overlay page screenshot-verified; app 463 green | S | S9-4 | integrator |
| S9-8 ✅ | "Stop screensaver" fixed (owner report: did nothing): ±1 px nudge < scrnsave anti-jitter threshold; replaced with enumerate-and-CloseMainWindow of `.scr` processes (covers self-launched savers SPI never reports; kill fallback for windowless; protected-process exceptions skipped) | Live probe: saver found + gone <1 s via the exact mechanism; app 463 green | S | S8-5 | integrator |

## Sprint 10 — public-launch preparation (owner-directed)

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S10-1 ✅ | Launch prep: VoiceRemote references purged (ADR-001 genericized, CLAUDE/README/BLUEPRINT/RESEARCH), doc-accuracy pass (README rewritten to launch structure: features/install/verify/docs map; BLUEPRINT .NET-10 + 0.17.7 drift fixed), OSS scaffolding (SUPPORT.md, CODEOWNERS, docs/launch-checklist.md with the CSA-trademark blocker), Inno installer (per-user, AppMutex, fabric-preserving uninstall) + release pipeline attaches Setup exe + portable zip + SHA256SUMS | git grep zero VoiceRemote hits; installer full cycle verified locally (install→boot+pair→uninstall, %APPDATA% intact); ISCC on runner | M | — | integrator |
| S10-2 ✅ | Trademark-safe logo: 3 original candidates rendered via --export-logo-candidates (helm / helm+house / bridge+house; no tri-radial motif) + SVG specimen artifact; owner picked A (helm) → Render() switched, CSA mark ARCHIVED in TrayIcons (kept per owner request, reference row in the candidates sheet); NOTICE/launch-checklist icon blocker resolved (name half stays open) | Contact sheet verified all states/sizes/themes; app 463 green | S | S10-1 | integrator |
| S10-3 ✅ | Remaining launch tasks that do not require going public: gitleaks full-history scan (89 commits, 0 leaks), E2E checklist re-pointed at the v0.3.0 release build + new sections (distribution/installer, macros+system commands, overlay theme/opacity), Dependabot config (grouped weekly npm/NuGet/Actions; matter.js deliberately ungrouped), repo description + 9 topics set, checklist updated to record what is cleared vs human-only | Scan output; YAML structure-validated; repo metadata verified via gh; repo remains PRIVATE | S | S10-1 | integrator |
| S10-4 ✅ | Portability review fix (ADR-009): per-install uniqueIdSeed resolved once and migration-safe (existing fabric → legacy constant so paired installs never re-pair; fresh → minted), VID/PID as real config + env + Settings→Advanced rows accepting hex/decimal | Live proof: upgraded the paired HTPC install → "pinned to the legacy shared seed" + hub re-subscribed; 3 seeds × 5 endpoints = 0 collisions; bridge 342 / app 497 green; settings demo screenshot-verified | M | S10-1 | integrator |
| S10-5 ✅ | "Store app…" picker in both launch editors (owner request: launch Spotify): AppLaunch.ListStoreApps over the execution-alias dir + StoreAppPickerDialog, since Browse… cannot reach Program FilesWindowsApps and package paths are CreateProcess-denied (S9-5) | Spotify launched live through the exact UseShellExecute=false path; 2 new tests (sorted/filtered listing, missing dir = empty); app 499 green; dialog screenshot-verified | S | S9-5 | integrator |
| S10-6 ✅ | Configurable bridge display name (owner request): DEFAULT_BRIDGE_NAME + BridgeOptions.bridgeName → root nodeLabel/productName, HTPC_BRIDGE_NAME env, config field + Settings→Devices row (label not identity, so no re-pair); two-PC guide corrected - separate PID is NOT required (root + endpoint identities already differ), only names | bridge 343 / app 500 green; Devices page screenshot-verified; identity distinctness recomputed for root+endpoints | S | S10-4 | integrator |

| S10-7 ✅ | Pairing/onboarding UX pass (owner request): PairingWindow rebuilt as a 3-stage wizard step (Starting / ReadyToScan / Paired) with numbered phone-side steps, live status strip, Console link — fixing the WRONG Home-app path it named ("Works with Google" = cloud account-linking, cannot add a Matter device), the MT:PENDING placeholder QR, and the dead post-commissioning QR; new WelcomeWindow first-run guide (config `onboardingShown`, fresh-install-only trigger, tray "Setup guide…") with a one-click enable+pair; state-aware tray tooltip | app 501 green (new onboardingShown round-trip/garbage test); --demo-pairing-window extended to 4 objective stage checks + 3 screenshots, --demo-welcome-window added (DPI-relative fits-768px + primary-button checks); all screenshots visually verified | M | S10-6 | integrator |
| S10-8 ✅ | Factory-reset re-pair fix (owner report: "can't find device" after reset): tray cached the pre-reset pairing code (invalid the moment the fabric is deleted — matter.js mints a new passcode/discriminator on restart), so it is now cleared on confirm; FactoryReset always restarts AND persists bridgeEnabled (an uncommissioned node that is not running advertises nothing); pairing window auto-opens on completion and swaps Starting→fresh code; stage + code-replacement logging added so a recurrence is diagnosable from the log | app 501 green (2 reset tests re-specified: disabled-bridge reset now comes up Connected + persists enabled; overlay copy); live log proves the restart already worked (20:47:03.936, fresh payload +4.5 s) so the stale code was the real defect | S | S10-7 | integrator |
| S10-9 ✅ | Windows mDNS query-response fix (owner report round 2: still cannot pair): matter.js 0.17.7 labels inbound mDNS packets with the numeric IPv6 scope-id while its record store is keyed by friendly interface names, so the bridge ANNOUNCES but never ANSWERS discovery queries — pairable only by luck during the post-start announcement burst. Contained wrap-and-delegate workaround at the adapter boundary (scope-id→name normalization, ADR-010); remove when upstream fixes it | adapter tests 7/7; worktree verify 350/350 green; empirical: pinned-interface node answers _matterc PTR +15–17 ms sustained past the burst (pre-fix control: RX proven via dgram shim, zero TX ever); live deployed bridge answered +13/+11 ms; local matter.js controller probe then commissioned the live bridge end-to-end (PASE_OK→COMMISSIONED_OK→DECOMMISSION_OK). shipped in 0.4.2 | M | — | integrator (Codex worker) |
| S10-10 ✅ | Pairing window self-diagnosis (owner request after the 3-cause pairing outage): show active VID/PID in hex on the ReadyToScan stage (must exactly match a Google Home Developer Console integration; hub reboot required after Console edits) + per-install identity-seed uniqueness note (never copy config.json between PCs); user-guide troubleshooting entries for 2.4 GHz-band client isolation (phone must join the 5 GHz SSID — discovery works, PASE silently dies) and Console VID/PID mismatch presenting as generic can-t-connect | shipped in 0.4.2 incl. follow-ups: recentering on stage change (S10-10b), live Paired flip + 4s auto-close + blue AwaitingPairing tray state per ADR-011 (S10-10c); app suite green | S | S10-9 | integrator |
| S10-11 ✅ | Tray auto-update (owner request): check GitHub releases (tag v*, assets Setup exe / portable zip / SHA256SUMS per release.yml) from the tray — manual check + daily background check with consent-gated download, SHA256 verification against the release manifest, installed-mode handoff to Inno /SILENT after clean app exit (AppMutex), portable-mode staged swap-and-relaunch helper; anonymous API once repo is public, optional MATTERHELM_UPDATE_TOKEN env (never persisted/logged) while private | shipped in 0.4.2; hardened by S10-14 security remediation (hash re-verify in helper, relaunch-on-failure, redirect token-strip pinned, mode detection vs running exe) | M | S10-10 | integrator |
| S10-13 ✅ | Power command fidelity (owner report: powerOffAction=displaysOff sleeps the PC instead): trace routing, fix the Modern-Standby cascade (hold ES_SYSTEM_REQUIRED while displays are commanded dark, release on toggle-ON/app exit/bridge disable), add "screensaver" as a powerOffAction (primitives exist in SystemCommands), keep symmetric toggle semantics (ON = displays-on+release / stopScreenSaver), settings UI option + user-guide | shipped in 0.4.2; root cause confirmed as Modern-Standby cascade (routing was correct); keep-awake release paths extended by S10-17A | M | S10-11 | integrator |
| S10-12 ✅ | Advertisement health monitor (co-session): bridge self-probes its commissionable mDNS advertisement and reports over the additive matterStatus frame (protocol v3); surfaced in pairing window + tray. Review remediations: monitor lifecycle across commissioning, default-route interface selection, dual-family IPv4+IPv6 probe, decommission pairing-code re-emission (S10-15/S10-17C) | shipped in 0.4.2; bridge verify 368 green | M | — | integrator |
| S10-16 ✅ | Overlay header = static product name; command identity (incl. custom commands) moved to the width-stable command row with ellipsis (S10-17B follow-up) | shipped in 0.4.2; overlay demo OVERALL: PASS | S | — | integrator |
| S10-17 ✅ | Pre-release deep review + remediation: 5 dimensions (bridge correctness, protocol drift, updater security, user flows, thin cross-family verification); 25 confirmed findings all fixed (S10-14/15/17A/17B/17C); final gates 606 app + 368 bridge green | shipped in 0.4.2 | L | S10-9..16 | integrator |
| S10-18 ✅ | Displays-off sleeps Modern Standby PCs despite the keep-awake hold (owner report; kernel events prove MS entry 2 s after acquire): DDC/CI hardware monitor power-off (VCP D6=0x04/0x01) as the primary path — OS never sees screen-off; blanking+hold fallback for non-DDC panels; per-monitor path logging | shipped in 0.4.3; 7 policy-matrix tests | M | S10-13 | integrator |
| S10-19 ✅ | Overlay single identity pill (command name / volume % / mute); static header retained | shipped in 0.4.3; demo PASS | S | S10-16 | integrator |
| S10-21 ✅ | mDNS interface adapter dropdown with Auto + "(not detected)" stale state (owner request after second-PC wired mis-pin) | shipped in 0.4.3; 66 focused tests | S | — | integrator |
| S10-20 🔜 partial | Advertising robustness: dead/misrouted pins are already surfaced by advertisement health. Remaining work is automatic fallback plus two-PC/per-network-profile firewall guidance. | Auto-fallback proven without hiding a bad explicit pin; current network-profile instructions hardware-checked | S | S10-21 | integrator |
| S10-21..25 ✅ | Adapter dropdown + filtering, onboarding streamlining (contextual menu, condensed pairing window, IPv6 requirement), scan-code key injection, DDC-honest display power | shipped in 0.4.4 | M | — | integrator |
| S10-26..28 ✅ | Deep review (3 sweeps + adversarial verification) and remediation: bridge backpressure/expectation rollback/transactional construction, app lifecycle serialization/macro hygiene/font disposal/keep-awake release/retention rollover, docs realignment | shipped in 0.4.4; bridge 375, app 689 | L | — | integrator |
| S10-29 ✅ reverted | Attempted timing-based trailing-Off suppression | Reverted by owner decision; timing cannot distinguish controller intent from echo | S | S10-28 | integrator |
| S10-30 ✅ | Retained switch state: Play/Pause distinct edges, Next/Previous both-edge, reversible/momentary Power split; ADR-012 | Protocol/mapping/app parity and behavior tests | M | S10-29 | integrator |
| S10-31 ✅ | Absolute dedicated Play/Pause through the current SMTC session; no appcommand fallback because it can invert intent; amend ADR-003 | Spotify/YouTube toggle evidence recorded; focused tests | M | S10-30 | integrator |
| S10-32 ✅ | Custom commands retained/both-edge by default with opt-in reset-after-activation; reset default 0; amend ADR-012 | Config/env/UI/mapping parity and tests | M | S10-30 | integrator |
| S10-35 ✅ (clean standard-suite + hardware verdict remain release gates, not documentation work) | 0.5.0 documentation truth pass across public docs, blueprint, ADRs, backlog, E2E and launch gates | All 66 audit findings resolved; current test evidence and Windows floor recorded without inventing a green verdict | M | S10-30..32 | docs |
| P-6 🔜 | Pin postject in bridge/package.json + lockfile so SEA release builds stop fetching it ad hoc via npx (deferred from S10-26: needs synchronized lockfile change) | open | S | — | integrator |
## Proposed (from agent reports, integrator-triaged)

- **P-7** (deferred by owner 2026-08-30, feasibility already established): make a
  Hue light-sync command STATE-AWARE instead of a blind hotkey toggle. Probed
  live: a Hue bridge (BSB003, apiversion 1.78.0) is reachable on the LAN;
  `GET /api/config` answers unauthenticated, while
  `/clip/v2/resource/entertainment_configuration` returns 403 without a key —
  with an application key it exposes `status: active|inactive`, which IS the
  sync state, and `/eventstream/clip/v2` pushes changes (no polling needed).
  The bridge can also STOP streaming directly, but cannot START the desktop
  app's sync — that still needs the Ctrl+Shift+V hotkey on the machine running
  Hue Sync. Scope options when picked up: (a) full state-aware switch — report
  live state, send the hotkey only when the desired state differs, stop via the
  bridge, blind-toggle fallback when unreachable; (b) read-only accurate
  reporting; (c) local assumed state, no dependency. Costs: one-time
  link-button pairing, an application key stored as a secret (never logged),
  self-signed-certificate handling, graceful degradation, and an ADR — it would
  be the product's first outbound network integration. Caveat: bridge state
  reflects ANY streaming source (Sync Box, a game, either PC), not one app.

- **P-5** (S7-2 merge observation): a few supervisor tests log through the
  static `Log` default directory, creating a stray `%APPDATA%\MatterHelm`
  during test runs (violates ground rule 4's spirit; also nearly confused the
  real migration). Route those tests through an injected log sink / temp
  `Log.LogDirectory`.

- ~~**P-1**~~ ✅ done (integrator): supervisor takes `extraEnv` (contract vars always win); BridgeHost passes `HTPC_BRIDGE_DEVICE_NAMES` JSON + `HTPC_BRIDGE_MDNS_INTERFACE` from config.
- **P-4**: tether robustness — when the tray app is hard-killed (not menu Exit), the sidecar was observed surviving ≥4s; index.ts listens for stdin "end"/"close" but a broken pipe may surface as "error". Add "error" handling + an S1-R check. *(S3-3 measurement 2026-08-09: packaged SEA sidecar exited 0.6 s after a tray hard-kill — did not reproduce; keep as a low-priority hardening item.)*
- ~~**P-2**~~ ✅ shipped in protocol v3 as `matterStatus`, including
  commissionable-advertisement health.
- ~~**P-3**~~ ✅ Factory reset is shipped in both the tray menu and Settings;
  it enables/restarts the bridge and opens a fresh pairing window.

## Icebox (explicitly not now)

- Kodi JSON-RPC target for transport/volume (fresh implementation here)
- Free VID/PID registration to remove the "Uncertified" pairing banner
- Media Playback cluster support (blocked on Google — watch release notes)
- Alexa/Apple Home multi-admin validation
- Native scene endpoints beyond today's custom-command switches. App-launch
  commands already publish one Matter endpoint per enabled custom command.
