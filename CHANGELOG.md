# Changelog

All notable changes to MatterHelm are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/) from
0.1.0 onward. The Matter *protocol* version (`bridge/src/ipc/protocol.ts`) is
versioned independently of the app/bridge SemVer and only ever bumped with an
ADR (see `docs/ENGINEERING-STANDARDS.md`).

## [Unreleased]

### Added

- Dedicated **Play** and **Pause** media-key options (S9-1) alongside the
  toggle: absolute verbs via Windows' appcommand channel, so "play" never
  pauses and "pause" never resumes - usable as custom commands and macro
  steps.

### Changed

- Devices & Commands compacted (S9-2): each built-in command is now ONE row
  - an enabled checkbox at the start of the line (untick greys the row and
  its name box - the disabled state reads at a glance), the description,
  and the device-name editor inline. Ten rows became five.
- Settings polish (S9-1): the per-row "takes effect" notes are factored into
  one footer message (rows carry a small marker instead); the overlay
  **Preview** now shows at the position you have staged in the window, not
  the last-saved one (and restores after the flash); the custom-command
  Action column fills the available width.

### Fixed

- Settings scrollbar no longer ranges far past the last row (the S9-2
  "excessive white space": a filler layout row inflated the scrollable
  height), and the mouse wheel now scrolls the page under the cursor even
  while an editor has focus - hover-scrolling can no longer spin a number
  field mid-scroll.
- Settings pages no longer snap their scroll position when a control is
  clicked or tabbed to (the WinForms scroll-on-focus jump; S9-1).

## [0.2.0] — 2026-08-19

Sprint 8: trigger mechanics and command power. Repeated commands work, taps
always fire, macros and system commands arrive, and the whole pipeline stops
queueing behind slow actions. No protocol, identity, or pairing change —
updating from 0.1.0 needs no re-pair.

### Added

- **System commands** as a custom-command action type (and macro step):
  start/stop screensaver, displays off/on, sleep, hibernate, lock, close the
  focused program (graceful WM_CLOSE), shut down, restart (S8-5). Shut
  down/restart act immediately with no PC-side confirmation.
- **Command sequences (macros)**: a custom command can now run several
  actions in order — media keys, program launches, key chords, and waits
  (1–5000 ms each; ≤16 steps, waits capped at 10 s total) — from a single
  voice command or tile tap. Built in the Add/Edit command dialog's new
  "Command sequence (macro)" type with an ordered, reorderable step list
  (S8-3). Execution stops at the first failing step and the log names it.

### Fixed

- **Macros no longer stall other commands** (S8-6, deep-review finding): a
  macro's waits used to run on the IPC receive loop — the WebSocket read
  loop itself — so a long macro froze every command behind it (volume,
  taps) and could hold app exit hostage for up to 10 s. Delay-bearing
  macros now run on a background runner (ack = "started", outcome via
  log + overlay), waits are cancelled instantly on app exit, and instant
  macros keep their precise inline ack.
- **Every tap on a momentary tile now fires** (S8-4). Google Home's tile is
  a toggle over Google's own state model, which lags the bridge's instant
  auto-reset — so a tap could arrive as an `Off` command and was dropped,
  leaving every other tap dead (and re-typing the device as "Switch" in the
  Home app changes only the icon). Momentary endpoints now treat any OnOff
  command as a press; consequently "turn **off** HTPC Next" also presses it.
  HTPC Power keeps distinct on/off meanings.

- Repeated identical commands are no longer dropped. Plug endpoints (the
  transport buttons, every custom command, and the power switch) now dispatch
  from the Matter **OnOff command** instead of the attribute change it caused,
  so "turn on HTPC Next" twice in a row skips twice and "turn off HTPC Power"
  fires even when the tile already reads off (ADR-008, S8-1). No protocol,
  identity or pairing change.

### Changed

- **Tray icon**: one glyph for every state — the Matter mark, tinted by bridge
  state (theme silhouette = off, amber = connecting, green = running, red =
  faulted). The green house composition is gone.
- The momentary auto-reset window (default 300 ms) is now presentation only —
  it returns the Home app tile to `off` after a press but no longer gates
  dispatch. One subscription report per press instead of two.
- Tap reset delay now accepts **0** (snap back to off immediately) — the
  floor was 100 ms while the reset was load-bearing; post-ADR-008 it's a pure
  UX knob (S8-2). Settings → Devices & Commands → "Tap reset delay (ms)".
- **Default tap reset delay is now 0 ms** (was 300): tiles snap back
  immediately, which also lets Google's device model accept rapid repeat
  presses sooner. Existing installs keep whatever value their config.json
  already stores.
- User guide documents Google's per-device **Type** re-typing (device tile →
  gear → Type → Switch) for anyone who'd rather see the momentary commands as
  switches than plugs — Home-app metadata only, no bridge change.

## [0.1.0] — 2026-08-09

First tagged release: everything from Sprints 0–7 plus the Sprint 3
hardening/ship stories (S3-1 packaging, S3-2 factory reset + user guide,
S3-3 release gate). Feature-complete and paired against real Google Home
hardware. High-level summary of what's shipped (see `README.md` "Status"
for the authoritative, up-to-date version):

### Packaging & release (Sprint 3)

- `build.ps1` produces a single self-contained `dist\` folder: Node SEA
  sidecar (`sidecar\bridge.exe`, with an automatic `node.exe + bridge.cjs`
  fallback layout) plus a self-contained single-file tray exe — no Node.js
  or .NET required on the target machine. Sizes: tray 111 MB, sidecar
  88 MB, total ~200 MB.
- Factory-reset flow (tray menu, deletes Matter storage after a spelled-out
  confirmation) and `docs/user-guide.md`; scripted hardware E2E checklist
  in `docs/e2e-log.md`.
- Tag-triggered release workflow (verify + tests + dist build + GitHub
  Release zip).
- Release perf/budget gate (ADR-007 method: private bytes, settled ≥ 2 min
  idle, packaged dist under test) — all budgets met:

  | Metric | Budget | Measured (2026-08-09) |
  |---|---|---|
  | Tray idle private bytes | ≤ 32 MB | 15.5 MB |
  | Sidecar processes | 1 | 1 (SEA `bridge.exe`) |
  | Sidecar idle private bytes | ≤ 120 MB | 93 MB |
  | Idle CPU (both) | < 0.5 % | tray 0.08 %, sidecar 0 % |
  | Sidecar cold start → "bridge started" | < 3 s | 2.04 s |
  | UI churn probe (`--probe-resources`) | PASS | PASS (all 4 bounds) |
  | `npm audit` | clean | 0 vulnerabilities |

  Bonus observation: after a hard tray-app kill the packaged sidecar exits
  via its stdin tether in 0.6 s (backlog watch item P-4 did not reproduce
  with the SEA layout).

### Bridge (`bridge/`)

- Full Matter device model behind the `matter/adapter.ts` boundary: an
  Aggregator exposing a Speaker (volume/mute) and momentary-switch endpoints
  for media/power actions, commissioned via QR against a real Nest hub.
- Protocol v2 (`bridge/src/ipc/protocol.ts`) with custom-command support
  (`HTPC_BRIDGE_ENDPOINTS`-driven dynamic endpoint construction), a pure
  `mapping/` layer translating cluster writes to IPC actions and back.
- Structured diagnostics: pino logging, session/subscription observability,
  per-action timing with correlation ids.
- esbuild single-file bundle (one Node process, ~0.9 s cold start).
- 322 tests; pure modules (`mapping/`, `ipc/protocol.ts`, `config.ts`) gated
  at ≥ 90% line coverage in CI.

### Tray application (`app/MatterHelm`)

- Sidecar supervisor (spawn, token auth, restart backoff, stdin tether) and
  loopback IPC server; `ActionExecutor` for CoreAudio volume/mute
  (with change observation), SMTC media keys, configurable key-sequence
  chords, app launch, and display power.
- Click-through, non-activating overlay HUD (incoming command + executed
  action, volume fill bar, update-in-place, DPI-safe at 200%).
- Settings window: categorized left nav, search, staged edits with
  validation, full custom-command CRUD including key-sequence capture, dark
  mode.
- Local metrics (action/ack/restart/reconnect counters) and a
  privacy-hardened diagnostics export (Settings → Advanced → Export
  diagnostics): scrubbed logs/manifest as a local-only zip, no automatic
  upload.
- 402 tests; measured resource budgets enforced (ADR-007: single sidecar
  process, WinForms-baseline memory, idle-churn probes).
- Renamed to **MatterHelm** (from the original working name) with an atomic
  `%APPDATA%` migration that preserves the paired Matter fabric.

### Process / governance

- Seven sprints delivered and adversarially reviewed (five dedicated review
  passes plus per-sprint review stories) — see `BACKLOG.md`.
- Open-source-grade project scaffolding: license, contributor/security/
  conduct docs, issue and PR templates, release automation (this change).

### Known gaps

- Continued hardware end-to-end validation, logged in `docs/e2e-log.md`
  (the full scripted checklist has not yet been executed against the
  packaged dist — needs the human, a Nest hub, and a phone).
