# Backlog

Stories sized for single-agent delegation. Rules: a story is delegated only
when its `Deps` are done and no concurrently running story shares its files.
Sizes: S ≤ half day · M ≤ 1 day · L ≤ 2 days. "Agent" is the suggested
executor profile (see CLAUDE.md for orchestration mechanics).

## Sprint 0 — foundations & de-risking

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S0-1 | Verify & pin the bridge scaffold: in `bridge/`, `npm install` matter.js (`@matter/main`), zod, pino, tsx + the eslint/prettier/vitest toolchain; make `npm run verify` pass on the placeholder; pin exact versions | `npm run verify` output green in report; lockfile committed; versions listed | S | — | impl (Sonnet) |
| S0-2 | CI: GitHub Actions `verify` (bridge) on windows-latest + ubuntu-latest | Green run demonstrated | S | S0-1 | impl (Sonnet) |
| S0-3 | **SPIKE** (re-scoped by ADR-002): with test VID/PID registered in a free Google Home Developer Console project + Nest hub on LAN, commission a minimal matter.js bridge (Speaker + one OnOff endpoint); verify Speaker volume UX (voice % + app slider) | Result documented in `docs/spikes/S0-3-pairing.md` with log evidence: console recipe, hub confirmation, Speaker UX verdict; **needs the human** for console signup + phone/Home-app steps | M | S0-1 | impl (Fable) + human |
| S0-4 | **SPIKE**: momentary-switch UX — auto-reset 800 ms OnOff endpoint; verify Home-app taps and voice register cleanly; ALSO commission one Generic Switch endpoint and record app/routine surfacing (ADR-002) | Findings + chosen reset interval + Generic Switch verdict in `docs/spikes/S0-4-momentary.md` | S | S0-3 | impl (Fable) + human |
| S0-5 ✅ | `app/` scaffold: `HtpcMatterBridge.csproj` (net8.0-windows, WinForms, single-instance Program.cs, Log.cs, empty TrayContext showing an icon), builds with warnings-as-errors | `dotnet build -c Release` 0 warnings; exe shows tray icon; report screenshot/log | S | — | impl (Sonnet) |

## Sprint 1 — the bridge (`bridge/`)

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S1-1 | `ipc/protocol.ts` + zod schemas + exhaustive unit tests (valid/invalid frames, version field, pairing msg) | ≥ 90 % coverage on the module; tests read as a protocol spec | S | S0-1 | impl (Sonnet) |
| S1-2 | `mapping/actions.ts` + `mapping/state.ts` pure functions + tests (incl. 0–254 ↔ 0–100 % rounding edges) | ≥ 90 % coverage; edge-case tests | S | S1-1 | impl (Sonnet) |
| S1-3 | `matter/adapter.ts` + `bridge.ts` + `devices.ts`: Aggregator with Speaker, 3 momentary switches, power toggle; persisted storage in configurable dir | Manual: all endpoints visible in the Home app; storage survives restart (no re-pair) | L | S0-3, S0-4, S1-2 | impl (Fable) |
| S1-4 | `ipc/client.ts`: WS client, token hello, jittered-backoff reconnect, graceful degradation when peer absent | Integration tests vs mock server: auth-reject closes, actions drop with one WARN when down, reconnect works (fake timers) | M | S1-1 | impl (Fable) |
| S1-5 | `config.ts` + `index.ts` composition root + pino logging + `pairing` message emission; config incl. `mdnsInterface` (BLUEPRINT §2.1 Windows notes) | `npm start` runs the full bridge against a mock tray-app peer; README quick-start true | S | S1-3, S1-4 | impl (Sonnet) |
| S1-R | Adversarial review of Sprint 1 (races, protocol drift, matter.js leakage past adapter, bloat) | Findings verified + fixed or explicitly waived | M | S1-5 | review (different model) |

## Sprint 2 — the tray application (`app/`)

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S2-1 | `Sidecar/`: `SidecarSupervisor` (spawn node/SEA exe, env token, restart backoff, stdin tether, stdout→log) + `IpcServer` (loopback WS, hello/auth, close on invalid frame) + `Protocol.cs` typed records | Protocol unit tests; chaos demo: kill sidecar → auto-restart with backoff logged; wrong token → socket closed | L | S0-5, S1-5 | impl (Fable) |
| S2-2 | `Actions/`: `ActionExecutor` + `MediaKeys` (SendInput VK_MEDIA_*) + `SystemVolume` (CoreAudio get/set/observe with change events) + `DisplayPower` | Manual demo: each action works on real media; volume observation fires state updates | M | S0-5 | impl (Fable) |
| S2-3 | `Ui/OverlayHud.cs`: persistent click-through non-activating flash window — primary line = incoming command ("Google Home → volume 40 %"), pill = executed action/failure; updates in place, fades; toggleable + persisted | Manual demo incl. rapid-fire updates without flicker; never steals focus or blocks clicks | M | S0-5 | impl (Sonnet) |
| S2-4 | `Ui/PairingWindow.cs` (QR rendered locally from `qrPayload` + manual code) + tray states (gray/green/amber/red) + full menu + `Config.cs` (device names, port, power mapping, overlay toggle) + Reload | Manual: enable → pair → green; names from config appear in Home app after re-pair; reload applies without restart | M | S2-1 | impl (Sonnet) |
| S2-5 | Wire it: IpcServer actions → executor → overlay flash → ack; state publisher (volume/mute on connect + on change) | **Exit demo evidence**: "Hey Google…" pauses real media; overlay flashes; Home-app slider tracks local volume change | M | S2-1, S2-2, S2-3, S2-4 | impl (Fable) |
| S2-R | Adversarial review of Sprint 2 (thread marshalling, supervisor races, P/Invoke correctness, protocol drift vs bridge) | Findings verified + fixed | M | S2-5 | review |

## Sprint 3 — hardening & ship

| ID | Story | Acceptance criteria | Size | Deps | Agent |
|---|---|---|---|---|---|
| S3-1 | Node SEA single-exe (`npm run package`) + `dotnet publish` self-contained app + root `build.ps1` producing one dist folder; ADR if SEA infeasible | Clean-machine run of the packaged dist pairs & controls; sizes reported | L | S2-R | impl (Fable) |
| S3-2 | Unpair / factory-reset flow (tray action deletes matter storage) + `docs/user-guide.md` | Scripted E2E checklist executed & logged in `docs/e2e-log.md` | M | S3-1 | impl (Sonnet) |
| S3-3 | Perf/budget pass (BLUEPRINT G6) + `npm audit` clean + release 0.1.0 | Budget table meets G6; tagged release | S | S3-2 | impl (Sonnet) |

## Icebox (explicitly not now)

- Kodi JSON-RPC target for transport/volume (fresh implementation here)
- Free VID/PID registration to remove the "Uncertified" pairing banner
- Media Playback cluster support (blocked on Google — watch release notes)
- Alexa/Apple Home multi-admin validation
- Additional endpoints: app-launch switches, "movie mode" scene endpoint
