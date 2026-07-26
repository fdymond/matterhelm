# Backlog

Stories sized for single-agent delegation. Rules: a story is delegated only
when its `Deps` are done and no concurrently running story shares its files.
Sizes: S ≤ half day · M ≤ 1 day · L ≤ 2 days. "Agent" is the suggested
executor profile (see CLAUDE.md for orchestration mechanics).

## Sprint 0 — foundations & de-risking

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S0-1 | Verify & pin the scaffold: `npm install` matter.js (`@matter/main`), zod, pino, vitest, eslint stack; make `npm run verify` pass on the placeholder; pin exact versions | `npm run verify` output green in report; lockfile committed; versions listed | S | — | impl (Sonnet) |
| S0-2 | CI: GitHub Actions `verify` on windows-latest + ubuntu-latest | Green run linked/logged on a test push | S | S0-1 | impl (Sonnet) |
| S0-3 | **SPIKE**: minimal OnOff virtual device (matter.js example) pairs with the real Google Home; answer the hub/border-router question | Pairing succeeds/fails documented in `docs/spikes/S0-3-pairing.md` with screenshots/log evidence + the hub answer; **needs the human for the phone/Home app steps** | M | S0-1 | impl (Fable) + human |
| S0-4 | **SPIKE**: momentary-switch UX — auto-reset 800 ms endpoint; verify Home app taps and voice register cleanly | Findings + chosen reset interval in `docs/spikes/S0-4-momentary.md` | S | S0-3 | impl (Fable) + human |

## Sprint 1 — the bridge

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S1-1 | `ipc/protocol.ts` + zod schemas + exhaustive unit tests (valid/invalid frames, version field) | ≥ 90 % coverage on the module; tests read as a protocol spec | S | S0-1 | impl (Sonnet) |
| S1-2 | `mapping/actions.ts` + `mapping/state.ts` pure functions + tests (incl. 0–254 ↔ 0–100 % rounding edges) | ≥ 90 % coverage; property-style edge tests | S | S1-1 | impl (Sonnet) |
| S1-3 | `matter/adapter.ts` + `bridge.ts` + `devices.ts`: Aggregator with Speaker, 3 momentary switches, power toggle; persisted storage in configurable dir | Manual: all endpoints visible in Home app; storage survives restart (re-pair NOT required) | L | S0-3, S0-4, S1-2 | impl (Fable) |
| S1-4 | `ipc/client.ts`: WS client, token hello, reconnect w/ jittered backoff, graceful degradation when peer absent | Integration tests vs mock server: auth-reject closes, actions queue-drop with WARN when down, reconnect works (fake timers) | M | S1-1 | impl (Fable) |
| S1-5 | `config.ts` + `index.ts` composition root + pino logging | `npm start` runs the full bridge against a mock peer; README quick-start true | S | S1-3, S1-4 | impl (Sonnet) |
| S1-R | Adversarial review of Sprint 1 (races, protocol drift, matter.js leakage past adapter, bloat) | Findings verified + fixed or explicitly waived in report | M | S1-5 | review (different model) |

## Sprint 2 — VoiceRemote integration (files live in ../windows-voice-control)

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S2-1 | `Integrations/MatterBridge/`: supervisor (spawn, env token, restart backoff, stdin-tether kill), WS server, protocol handling | Unit-testable protocol part covered; kill/restart chaos manually demonstrated | L | S1-5 | impl (Fable) |
| S2-2 | Dispatch into `CommandRouter.HandleBindingAsync` + state publisher (volume/mute on connect + on change) | E2E: sidecar action pauses real media; Home app slider tracks volume changed locally | M | S2-1 | impl (Fable) |
| S2-3 | Config (`MatterBridgeConfig`) + tray toggle + pairing QR overlay on first enable | Enable → QR shows → pairing completes; toggle off stops sidecar; config persists | M | S2-1 | impl (Sonnet) |
| S2-R | Adversarial review of Sprint 2 + full VoiceRemote regression (existing speech harness still green) | Findings verified; harness output in report | M | S2-3 | review |

## Sprint 3 — hardening & ship

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S3-1 | Node SEA single-exe build (`npm run package`) wired into VoiceRemote's build.sh; ADR if SEA proves infeasible | Clean-machine run of the packaged exe pairs & controls; sizes reported | L | S2-R | impl (Fable) |
| S3-2 | Unpair/re-pair + factory-reset flow (delete storage via tray) + docs `docs/user-guide.md` | Scripted E2E checklist executed & logged in `docs/e2e-log.md` | M | S3-1 | impl (Sonnet) |
| S3-3 | Perf/budget pass (idle CPU, RSS, cold start) + `npm audit` + release 0.1.0 | Budget table in report meets BLUEPRINT G5; tagged release | S | S3-2 | impl (Sonnet) |

## Icebox (explicitly not now)

- Free VID/PID registration to remove the "Uncertified" pairing banner
- Media Playback cluster support (blocked on Google — watch release notes)
- Alexa/Apple Home multi-admin validation
- Wyoming-satellite exposure of VoiceRemote (different feature entirely)
