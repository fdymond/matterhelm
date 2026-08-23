# Changelog

All notable changes to MatterHelm are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/) from
0.1.0 onward. The Matter *protocol* version (`bridge/src/ipc/protocol.ts`) is
versioned independently of the app/bridge SemVer and only ever bumped with an
ADR (see `docs/ENGINEERING-STANDARDS.md`).

## [Unreleased]

### Added

- **First-run setup guide** (S10-7): a fresh install now opens a Welcome
  window with the three things that have to happen in order (register once
  with Google, name your devices, enable & pair), a link straight to the
  Developer Console, and one button that enables the bridge and opens the
  pairing window. Shown once on an install that has never paired; reopen it
  any time from the tray menu → **Setup guide…**.
- The pairing window now has **numbered phone-side instructions and a live
  status line**, and the tray tooltip spells out what the icon colour means.
- **Bridge name** (S10-6): Settings → Devices → **Bridge name** sets what
  Google Home calls the bridge itself, so several PCs running MatterHelm in
  one home are tellable apart. It's a label, not identity — changing it
  never re-pairs.

### Fixed

- **Re-pairing after a factory reset failed with "can't find device"**
  (S10-8): the tray kept serving the pre-reset pairing code. It belongs to the
  fabric the reset deletes, and matter.js mints a new passcode/discriminator
  on the next start, so scanning it sent the phone looking for a device that
  no longer existed. The cached code is now dropped the moment a reset is
  confirmed, and the pairing window opens by itself and swaps to the fresh
  code as soon as the sidecar reports it.
- A factory reset now always leaves the bridge **running**, including when it
  was switched off beforehand — an uncommissioned node that is not running
  advertises nothing to discover.
- **The pairing window named the wrong Home-app path** (S10-7): it said to
  choose "Works with Google", which is the cloud account-linking branch — a
  Matter device can never be added that way. It now names the real path:
  **+ → Add device → Matter-enabled device**.
- The pairing window no longer shows a code that cannot work: with no code
  yet it says the bridge is still starting (previously it rendered a real but
  meaningless `MT:PENDING` QR), and once commissioned it says so and points at
  Factory reset instead of leaving a dead QR on screen for a scan that can
  only fail.
- A pairing code arriving at an already-open window now reveals it, instead of
  rendering the QR inside a still-hidden panel.

### Changed

- README is now self-sufficient for setup: a numbered from-scratch walkthrough
  (requirements → free Console registration → install & verify → naming →
  pairing → first voice command) plus a "Multiple PCs in one home" section.
  The user guide stays the deeper reference.
- The multi-PC guide is corrected: a second PC needs **no** separate
  Vendor/Product ID and no extra Developer Console registration (per-install
  identity already keeps them distinct) — only distinct names. Adds an
  ordered setup walkthrough and a note for cloned machines.

## [0.4.0] — 2026-08-23

Multi-PC and multi-account readiness, from a pre-launch portability review.
Every install now has its own Matter identity, Vendor/Product IDs are real
settings, and Store apps like Spotify can be picked straight from the launch
editor. Upgrading preserves your pairing - no re-pair needed.

### Added

- **"Store app…" picker for launch commands** (S10-5): pick Spotify, Media
  Player, or any Microsoft Store app straight from the Add/Edit command
  dialog. Store apps live in a folder Browse… cannot open and must be
  launched through the shortcut Windows keeps for them — the picker fills
  in the right path for you.

- **Per-install Matter identity + configurable VID/PID** (ADR-009, S10-4).
  Every install used to derive identical endpoint identities from a
  compiled-in seed, so two PCs in one home advertised colliding Matter
  `UniqueID`s. Fresh installs now mint their own identity; installs that are
  already paired keep theirs, so upgrading never unpairs you. Vendor and
  Product IDs are real settings (Settings → Advanced, hex or decimal) - give
  a second PC its own PID from the test range and pair both.

### Changed

- Repo hygiene for the eventual public launch (S10-3): Dependabot config
  (grouped weekly npm/NuGet/Actions updates), repo description + topics,
  and the E2E checklist re-pointed at the v0.3.0 release build with new
  sections for the installer, macros/system commands, and overlay theming.
  Full-history secret scan: clean (89 commits, no leaks).

## [0.3.0] — 2026-08-23

Sprints 9-10: settings/overlay refinement and public-launch readiness. New
helm app icon (trademark-safe), overlay theming and opacity, dedicated
play/pause, restructured settings, live resource monitoring, a Windows
installer alongside the portable zip, and docs rebuilt for a public
audience. No protocol, identity, or pairing change - updating needs no
re-pair.

### Added

- **Windows installer** (S10-1): `MatterHelm-Setup-<version>.exe` (Inno
  Setup) - per-user by default (no UAC), optional start-with-Windows,
  Start-menu entry, clean uninstall that preserves pairing/settings.
  Releases now attach installer + portable zip + `SHA256SUMS.txt`.

- **Live resource monitoring** (S9-6): the 60 s metrics snapshots now carry
  process gauges - private bytes (the ADR-007 budget metric), managed-heap
  bytes, handle count, thread count - so a leak shows as a trend in
  `metrics-*.jsonl` instead of needing Task Manager. Idle days still cost
  one line; a quiet app additionally writes when private bytes drift ≥10 %.

- **Overlay theme and transparency** (S9-4): the overlay follows the
  Windows light/dark apps setting by default (resolved per flash, so a
  mid-session theme flip is honored), with Settings → Overlay options to
  force Light or Dark and an opacity slider (30-100 %). Preview shows the
  staged theme/opacity/position before saving. The app windows already
  follow the system theme.
- Dedicated **Play** and **Pause** media-key options (S9-1) alongside the
  toggle: absolute verbs via Windows' appcommand channel, so "play" never
  pauses and "pause" never resumes - usable as custom commands and macro
  steps.

### Changed

- Settings navigation restructured (S9-3): "Devices & Commands" is now
  **Devices** - a "Google Home devices" section of clean, description-free
  rows plus the power/tap-reset options - and custom commands moved to
  their own **Custom devices** section with a full-height list.
- Devices & Commands compacted (S9-2): each built-in command is now ONE row
  - an enabled checkbox at the start of the line (untick greys the row and
  its name box - the disabled state reads at a glance), the description,
  and the device-name editor inline. Ten rows became five.
- Overlay opacity is edited with a slider with a live percent readout
  instead of a number spinner (S9-7).
- Settings polish (S9-1): the per-row "takes effect" notes are factored into
  one footer message (rows carry a small marker instead); the overlay
  **Preview** now shows at the position you have staged in the window, not
  the last-saved one (and restores after the flash); the custom-command
  Action column fills the available width.

### Fixed

- **"Stop screensaver" now works** (S9-8): it used a net-zero 1 px mouse
  nudge that sits below Windows' anti-jitter dismissal threshold, so it did
  nothing. It now closes the running `.scr` process gracefully - which also
  covers savers our own "Start screensaver" launched (invisible to
  `SPI_GETSCREENSAVERRUNNING`).
- **Store-app launches** (S9-5): launching a Microsoft Store (MSIX) app by
  its package path (e.g. Spotify under `Program FilesWindowsApps`) failed
  with "Access is denied" - Windows refuses CreateProcess there by design.
  Launch actions now redirect to the app's per-user execution alias
  automatically (with an actionable error if the alias is disabled), and a
  failed launch's nack no longer reads "failed: launched X.exe".
- **Overlay theme preview** (S9-5): previewing a staged theme change kept
  showing the old panel - the HUD only re-rendered when the flash CONTENT
  changed, and every preview has identical content. The render is now
  palette-aware, which also makes a mid-session Windows theme flip repaint
  repeat flashes correctly.
- Mouse-wheel scrolling works again on settings pages (S9-3): the S9-2
  wheel filter computed the scroll itself and silently did nothing - it now
  forwards the wheel message to the hovered page and lets WinForms scroll.
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
