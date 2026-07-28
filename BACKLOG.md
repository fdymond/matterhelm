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
| S0-4 | **SPIKE**: momentary-switch UX — auto-reset 800 ms OnOff endpoint; verify Home-app taps and voice register cleanly; ALSO commission one Generic Switch endpoint and record app/routine surfacing (ADR-002) | Findings + chosen reset interval + Generic Switch verdict in `docs/spikes/S0-4-momentary.md` | S | S0-3 | impl (Fable) + human |
| S0-5 ✅ | `app/` scaffold: `HtpcMatterBridge.csproj` (net8.0-windows, WinForms, single-instance Program.cs, Log.cs, empty TrayContext showing an icon), builds with warnings-as-errors | `dotnet build -c Release` 0 warnings; exe shows tray icon; report screenshot/log | S | — | impl (Sonnet) |

## Sprint 1 — the bridge (`bridge/`)

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S1-1 ✅ | `ipc/protocol.ts` + zod schemas + exhaustive unit tests (valid/invalid frames, version field, pairing msg) | ≥ 90 % coverage on the module; tests read as a protocol spec | S | S0-1 | impl (Sonnet) |
| S1-2 ✅ | `mapping/actions.ts` + `mapping/state.ts` pure functions + tests (incl. 0–254 ↔ 0–100 % rounding edges) | ≥ 90 % coverage; edge-case tests | S | S1-1 | impl (Sonnet) |
| S1-3 ✅ (Home-app endpoint visibility validated at integration; S0-4 interval kept at 800 ms pending spike) | `matter/adapter.ts` + `bridge.ts` + `devices.ts`: Aggregator with Speaker, 3 momentary switches, power toggle; persisted storage in configurable dir | Manual: all endpoints visible in the Home app; storage survives restart (no re-pair) | L | S0-3, S0-4, S1-2 | impl (Fable) |
| S1-4 ✅ | `ipc/client.ts`: WS client, token hello, jittered-backoff reconnect, graceful degradation when peer absent | Integration tests vs mock server: auth-reject closes, actions drop with one WARN when down, reconnect works (fake timers) | M | S1-1 | impl (Fable) |
| S1-5 ✅ (full bridge ran vs mock tray peer: hello/pairing/state + tether shutdown) | `config.ts` + `index.ts` composition root + pino logging + `pairing` message emission; config incl. `mdnsInterface` (BLUEPRINT §2.1 Windows notes) | `npm start` runs the full bridge against a mock tray-app peer; README quick-start true | S | S1-3, S1-4 | impl (Sonnet) |
| S1-6 ✅ (violations demonstrably fail; resolver dep required and pinned) | ESLint import-boundary enforcement per ENGINEERING-STANDARDS: `matter/` ↛ `ipc/` (and vice versa), `@matter/*` imports only in `matter/adapter.ts`, `ws`/socket types only behind `ipc/` (via `import-x/no-restricted-paths` or `no-restricted-imports`; add `eslint-import-resolver-typescript` only if needed) | Lint demonstrably fails on a violation (show output), then verify green | S | S1-3, S1-4 | impl (Sonnet) |
| S1-R | Adversarial review of Sprint 1 (races, protocol drift, matter.js leakage past adapter, bloat) | Findings verified + fixed or explicitly waived | M | S1-5 | review (different model) |

## Sprint 2 — the tray application (`app/`)

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S2-1 ✅ (pulled forward of S1-5: protocol + env contracts were already fixed; chaos-tested vs stub children) | `Sidecar/`: `SidecarSupervisor` (spawn node/SEA exe, env token, restart backoff, stdin tether, stdout→log) + `IpcServer` (loopback WS, hello/auth, close on invalid frame) + `Protocol.cs` typed records | Protocol unit tests; chaos demo: kill sidecar → auto-restart with backoff logged; wrong token → socket closed | L | S0-5, S1-5 | impl (Fable) |
| S2-2 ✅ | `Actions/`: `ActionExecutor` + `MediaKeys` (SendInput VK_MEDIA_*) + `SystemVolume` (CoreAudio get/set/observe with change events) + `DisplayPower` | Manual demo: each action works on real media; volume observation fires state updates | M | S0-5 | impl (Fable) |
| S2-3 ✅ | `Ui/OverlayHud.cs`: persistent click-through non-activating flash window — primary line = incoming command ("Google Home → volume 40 %"), pill = executed action/failure; updates in place, fades; toggleable + persisted | Manual demo incl. rapid-fire updates without flicker; never steals focus or blocks clicks | M | S0-5 | impl (Sonnet) |
| S2-4 ✅ (pair-to-green + Home-app name checks deferred to hardware E2E) | `Ui/PairingWindow.cs` (QR rendered locally from `qrPayload` + manual code) + tray states (gray/green/amber/red) + full menu + `Config.cs` (device names, port, power mapping, overlay toggle) + Reload | Manual: enable → pair → green; names from config appear in Home app after re-pair; reload applies without restart | M | S2-1 | impl (Sonnet) |
| S2-5 ✅ (mock-sidecar E2E PASS; "Hey Google" hop deferred to hardware E2E) | Wire it: IpcServer actions → executor → overlay flash → ack; state publisher (volume/mute on connect + on change) | **Exit demo evidence**: "Hey Google…" pauses real media; overlay flashes; Home-app slider tracks local volume change | M | S2-1, S2-2, S2-3, S2-4 | impl (Fable) |
| S2-R ✅ (Opus review: 1 confirmed bug + 2 verified risks, all fixed; COM/threading/security clean) | Adversarial review of Sprint 2 (thread marshalling, supervisor races, P/Invoke correctness, protocol drift vs bridge) | Findings verified + fixed | M | S2-5 | review |

## Sprint 3 — hardening & ship

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S3-1 | Node SEA single-exe (`npm run package`) + `dotnet publish` self-contained app + root `build.ps1` producing one dist folder; ADR if SEA infeasible | Clean-machine run of the packaged dist pairs & controls; sizes reported | L | S2-R | impl (Fable) |
| S3-2 | Unpair / factory-reset flow (tray action deletes matter storage) + `docs/user-guide.md` | Scripted E2E checklist executed & logged in `docs/e2e-log.md` | M | S3-1 | impl (Sonnet) |
| S3-3 | Perf/budget pass (BLUEPRINT G6) + `npm audit` clean + release 0.1.0 | Budget table meets G6; tagged release | S | S3-2 | impl (Sonnet) |

## Sprint 4 — settings UI, custom commands, modernization (ADR-004/ADR-005)

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S4-0 ✅ | TFM bump `net10.0-windows` (both csproj) + CI dotnet 10.0.x; PerMonitorV2 confirmed | Build+tests green; `--demo-overlay`/`--demo-wired` re-run PASS (exit 0 both) | S | — | integrator |
| S4-1 ✅ | Bridge: `HTPC_BRIDGE_ENDPOINTS` env (ADR-004 shape) → dynamic endpoint construction (disabled built-ins omitted, custom momentary plugs); protocol v2 (`v:2` + `custom` action w/ `key`) in protocol.ts + mapping | verify green; unit tests for endpoint-set derivation + custom action mapping; mock-run shows custom endpoint write → `{name:"custom",key}` frame | M | S4-0 | impl (Fable) |
| S4-2 ✅ | App: Config `commands` schema + migration from `deviceNames`; Protocol.cs v2 parity; executor custom actions (`mediaKey`, `launch`); supervisor env → ENDPOINTS | dotnet tests incl. migration + v2 parity + custom dispatch; wired demo extended with a custom command round-trip | M | S4-0 | impl (Fable) |
| S4-3 ✅ (incl. PairingWindow dark-theming + production SetColorMode by integrator) | App: `Ui/SettingsWindow` per ADR-004 (left nav categories, top search filter, staged edits + validation, custom command CRUD editor, dark mode via SetColorMode) | Build 0 warnings; view-model/filter logic unit-tested; screenshot evidence light+dark; all settings round-trip to config.json | L | S4-2 | impl (Fable) |
| S4-4 ✅ (zero suppressions; behavior-preserving, demos re-run PASS) | Modernization refactor per ADR-005 (both projects): TimeProvider, LibraryImport, STJ source-gen, Lock, collection exprs, async-void audit, AnalysisMode=Recommended triage | verify + dotnet tests green at new analyzer bar; no behavior changes (demos re-run PASS) | L | S4-1, S4-2, S4-3 | impl (Fable) |
| S4-R ✅ (Opus: 0 confirmed bugs, protocol parity verified empirically; 2 RISKs + NITs fixed by integrator) | Adversarial review of Sprint 4 (protocol v2 parity, settings UX correctness, migration safety, refactor regressions) | Findings verified + fixed | M | S4-4 | review (Opus) |

## Proposed (from agent reports, integrator-triaged)

- ~~**P-1**~~ ✅ done (integrator): supervisor takes `extraEnv` (contract vars always win); BridgeHost passes `HTPC_BRIDGE_DEVICE_NAMES` JSON + `HTPC_BRIDGE_MDNS_INTERFACE` from config.
- **P-4**: tether robustness — when the tray app is hard-killed (not menu Exit), the sidecar was observed surviving ≥4s; index.ts listens for stdin "end"/"close" but a broken pipe may surface as "error". Add "error" handling + an S1-R check.
- **P-2**: additive protocol signal for commissioned/uncommissioned so tray green can mean "fabric joined" rather than "sidecar link up" (needs `v` bump, both sides).
- **P-3**: "Factory reset bridge" tray action (delete matter storage; BLUEPRINT §2.5) — schedule with S3-2 unpair flow.

## Icebox (explicitly not now)

- Kodi JSON-RPC target for transport/volume (fresh implementation here)
- Free VID/PID registration to remove the "Uncertified" pairing banner
- Media Playback cluster support (blocked on Google — watch release notes)
- Alexa/Apple Home multi-admin validation
- Additional endpoints: app-launch switches, "movie mode" scene endpoint
