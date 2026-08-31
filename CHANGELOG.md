# Changelog

All notable changes to MatterHelm are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/) from
0.1.0 onward. The Matter *protocol* version (`bridge/src/ipc/protocol.ts`) is
versioned independently of the app/bridge SemVer and only ever bumped with an
ADR (see `docs/ENGINEERING-STANDARDS.md`).

## [0.7.1] — 2026-09-01

Field fixes from real HTPC logs, plus a first-show centring bug the logs
indirectly exposed.

### Fixed
- **A screensaver-off step no longer aborts the rest of a command sequence.**
  Dismissing the screensaver is the action's contract; restoring focus
  afterwards is best effort. A refused restore used to fail the step — and
  report the success text "screensaver dismissed" as the failure reason — so a
  macro of screensaver-off → wait → play stopped at step one.
- **Focus restore now retries** (every 100 ms for up to 1.5 s). Immediately
  after the screensaver process closes there is often no foreground window at
  all, so the first attempt could be refused when the same restore succeeded a
  moment later.
- **A media command aimed at one app can no longer act on another.** When a
  delivery targeted at the focused player timed out, the fallback could apply
  the verb to whatever media session happened to exist — in practice starting a
  background Spotify session instead of the player being watched. A fallback is
  now only permitted to a session owned by the same app; otherwise the command
  fails honestly.
- **A timed-out delivery is retried once** after 200 ms, within a four-second
  route deadline: a window that has just been restored to the foreground is
  often still too busy to answer the first message.
- **Failure messages describe the failure** instead of echoing the intended
  action's success text.
- **The pairing window no longer opens slightly off-centre.** CenterScreen
  combined with content-driven auto-sizing centred the window before layout
  settled and left it about 47 px off; it now re-centres from the settled size
  after the first show.

### Changed
- The custom-command sequence editor has a single **Add…** button whose type
  dropdown now includes **Mouse move**; adding and editing all step types share
  one dialog and one target picker.

### Documentation
- Troubleshooting for `SendInput … (Win32 error 5)`: Windows refuses synthetic
  input into an elevated window, so a hotkey command cannot reach an app run as
  administrator (Philips Hue Sync is the common case). Run the target app
  non-elevated.


## [0.7.0] — 2026-08-31

### Added
- **Mouse movement inside command sequences.** A custom command's macro can
  now include a mouse step, offered in the sequence editor with the same
  target picker as the standalone command. Each step is a one-shot absolute
  move — there is no implicit restore, so add a second step if you want the
  pointer put back. Macro steps cannot disturb the position a standalone
  retained mouse command returns to.

### Fixed
- **Media commands no longer miss the player after a screensaver.** Starting
  the screensaver now records which window had focus, and stopping it hands
  focus back. Previously a fullscreen player left unfocused by the screensaver
  would be skipped, and the command hit a background app or failed. Focus
  restoration validates the window handle, its process id and process name
  before acting — handles are recycled, and focusing an unrelated app would be
  worse than doing nothing — and a refusal by Windows is logged rather than
  reported as success.

> **Limitation.** Dismissing the screensaver yourself with the mouse or
> keyboard does not run MatterHelm's stop path, so nothing triggers the focus
> restore. It applies when the screensaver is turned off through Google Home.
> Displays-off modes are unaffected: they never take focus away.


## [0.6.0] — 2026-08-30

### Added
- **Move the mouse** command category: virtual-screen presets (corners, centre)
  or explicit coordinates, clamped so a command can never strand the pointer
  off-desktop. Retained switch — ON captures the current position and moves,
  OFF puts it back.

### Fixed
- **Media commands now try the focused app first.** Players that do not publish
  a Windows media session — Kodi is the measured example — received nothing
  from the dedicated verbs. They are now sent to the focused window first,
  verified, and only then routed to a media session.
- **Commands can no longer act on the wrong application.** Verification read
  the GLOBAL media session, so with Kodi playing and a stale paused session
  belonging to another app, a dedicated Pause concluded "already paused" and
  did nothing — while an earlier build would instead start that other app
  playing. Verification is now scoped to the focused app; a session owned by
  someone else counts as unverifiable rather than as evidence.
- playPause no longer falls back to a toggle, which could cancel out a focused
  toggle that arrived late.
- An immediately repeated dedicated verb to the same unverifiable target is
  suppressed. Kodi honours PLAY absolutely but treats PAUSE as a toggle, and
  offers no session to verify against.

### Changed
- IPC protocol 4 -> 5 (additive): the custom action frame now carries the
  switch edge, which retained ON/OFF commands need. Tray and sidecar ship
  together, so no user action is required.
- Docs corrected where they still described media routing as session-only
  (BLUEPRINT, ADR-003, README); ADR-013 records the final model.

> **Known limitation.** For a player with no media session, success means the
> command was delivered, not that playback changed — Google receives an OK and
> the overlay shows success either way. The log names the path taken
> (focused-handled / session-fallback / unverifiable) for diagnosis.


## [0.5.1] — 2026-08-30

Packaging fix for 0.5.0. Two files that belonged to the 0.5.0 change set were
left unstaged and therefore missing from that release:

### Fixed
- The installer now enforces . 0.5.0 targets the
  Windows media-session APIs introduced in Windows 10 1809, but its published
  Setup exe would still install on older builds and then fail at runtime.
  Installing 0.5.1 on an unsupported build is now refused cleanly up front.

### Changed
- README brought in line with 0.5.0 behaviour (retained switches, the media
  session in the action path, current feature list and gate evidence).

## [0.5.0] — 2026-08-30

This section is the 0.5.0 release candidate. Version and release date remain
unset until tagging.

### Upgrade notes from 0.4.x

- Existing custom commands have no `resetAfterActivation` field. In 0.5.0
  they therefore become **retained switches that execute on either user
  transition**. For one-shot behavior, edit each affected command in
  **Settings → Custom devices** and enable **Reset the switch after it runs
  (momentary button)**; only On then executes and the tile returns to Off.
- **Play Pause** changes from a momentary toggle to retained state: On requests
  Play and Off requests Pause. Absolute verbs use the current Windows SMTC
  session. If no usable session exists, the request fails with a warning;
  MatterHelm deliberately sends no appcommand fallback because it can invert
  the requested intent.
- **Power** now models awake state. Displays-off,
  pause-plus-displays-off, and screensaver modes are reversible: Off engages,
  On reverses, and pause-plus-displays-off never resumes playback. Sleep is
  momentary: Off fires once and the tile promptly returns to On.
- IPC message revision moves from v3 to v4 for additive `play` and `pause`
  action variants; handshake protocol remains 1. The matching tray and
  sidecar ship together, so users take no action. A stale sidecar left running
  is rejected and logs a version mismatch.

### Added

- Per-custom-command **Reset after activation** opt-in. Retained/both-edge is
  the default; reset mode executes only On and uses the configured tap-reset
  delay (default 0 ms / next tick).
- Dedicated protocol-v4 Play and Pause actions, plus bounded absolute SMTC
  execution. Measurements found
  `APPCOMMAND_MEDIA_PLAY` toggles in both Spotify and YouTube, so it is not
  treated as absolute or used as a fallback.
- Reversible screensaver Power mode and full Power On handling for reversible
  display/screensaver modes.

### Changed

- Play/Pause, Next, Previous, reversible Power, and default custom-command
  endpoints retain their Matter OnOff state. Next/Previous and retained custom
  commands execute once on either controller transition.
- Irreversible Power modes (currently sleep) execute only an Off request, then
  locally reset the tile to On without dispatching another action.
- First-run bridge control is contextual: unpaired installs show **Pair with
  Google Home…**, which starts and persists the bridge; commissioned installs
  show **Enable bridge**.
- Tray state legend is gray/disabled, amber/starting, blue/awaiting pairing,
  green/connected, and red/crash-loop or missing advertisement.

### Documentation

- Reconciled the user guide, distribution quick start, architecture blueprint,
  ADRs, backlog, E2E script, launch gate, routines, and contributor instructions
  with the shipped 0.5.0 behavior.
- Current test inventory is 1,111 (391 bridge + 720 app). The documentation
  sandbox passed the alternate bridge runner 391/391, but standard Vitest
  startup was blocked by `spawn EPERM` and the app run was not green because
  loopback `HttpListener` tests fail in this sandbox. Clean standard-command
  release evidence remains required.

## [0.4.4] — 2026-08-28

A deep-review release: three read-only sweeps (bridge lifecycle, app
lifecycle/memory, cross-cutting cleanup) plus adversarial verification of
every behavioural claim, then remediation of all confirmed findings — on top
of a batch of onboarding and hardware-honesty work.

### Fixed
- Sidecar could be terminated by an unhandled rejection: a failed speaker
  write floated its rejection at the composition root (Node 22 exits on
  that). Now logged and handled — and the echo-suppression expectation it
  left behind, which could silently swallow a later genuine volume command,
  is rolled back (S10-27).
- IPC sends had no backpressure; an open-but-unreading tray peer could
  buffer without bound. A 2 MiB ceiling now closes the unhealthy socket and
  lets the normal reconnect recover (S10-27).
- Bridge lifecycle operations could race: rapid off/on could fail to bind
  the listener and leave bridgeEnabled=true persisted while the host stayed
  disabled. All lifecycle operations are serialized through one queue
  (S10-28).
- Matter construction is transactional: partial failures close the partly
  built node instead of wedging the process-global claim (S10-27).
- Keyboard chords carry scan codes, so vendor hotkey listeners (e.g. Philips
  Hue Sync) receive them (S10-24).
- Display-off no longer holds the machine awake after a failed blanking
  call; macro delays no longer block thread-pool threads; window fonts are
  disposed; log/metrics retention prunes on date rollover with size-based
  rollover (S10-28).

### Changed
- Onboarding streamlined: contextual tray menu (Pair while unpaired — which
  starts the bridge itself — Enable bridge once paired) and a condensed
  pairing window whose long-form setup guidance, including the IPv6
  requirement, moved into a collapsed section (S10-23).
- mDNS interface selection is a filtered adapter dropdown with Auto, IPv4
  annotations, a Show-all escape hatch and visible not-detected/hidden
  states (S10-21, S10-22).
- Displays-off states its real behaviour per hardware: DDC-capable monitors
  power off cleanly, and machines without DDC are labelled as entering
  standby, with the overlay warning only when the fallback ran (S10-25).
- Documentation realigned with shipped behaviour; Node floor raised to
  22.13 (matter.js requirement); duplicate dependency install removed from
  release CI (S10-26).

## [0.4.3] — 2026-08-27

### Fixed
- **"Turn displays off" no longer sleeps Modern Standby PCs (S10-18)**:
  0.4.2's keep-awake hold cannot veto S0ix standby (screen-off IS the
  trigger — confirmed via kernel power events). Compatible monitors are now
  powered off at the hardware level via DDC/CI, so the OS never sees a
  screen-off and playback continues; non-DDC panels fall back to the legacy
  blanking + hold, and mixed setups leave non-DDC panels on. The path taken
  is logged per monitor.

### Changed
- Overlay simplified to a single identity pill: command name, or volume
  percentage / mute state for the speaker; static MatterHelm header (S10-19).
- mDNS network interface is now an adapter dropdown with
  "Auto (recommended)", per-adapter IPv4 annotations, and a visible
  "(not detected)" state for stale saved adapters — a mistyped or outdated
  pin can no longer silently break advertising (S10-21).

## [0.4.2] — 2026-08-26

The release that makes pairing actually work. A field-debugging session on the
owner's network uncovered that matter.js 0.17.7 on Windows never answers mDNS
queries — devices were only ever discoverable by luck during the announcement
burst after startup (S10-9, ADR-010). With that fixed, the whole re-pairing
story got hardened end to end, an auto-updater arrived, and a five-dimension
pre-release review swept 25 confirmed findings out of the tree.

### Fixed
- **Windows mDNS discovery (S10-9, ADR-010)**: matter.js labels inbound mDNS
  packets with the numeric IPv6 scope-id while its record store uses friendly
  interface names, so the bridge announced but never answered a single query.
  A contained adapter-boundary workaround normalizes the identifiers; verified
  empirically (query answers in ~15 ms sustained, end-to-end commissioning by
  a local controller). Remove when fixed upstream.
- Power command respects the configured action: "turn displays off" no longer
  sleeps Modern-Standby machines — a keep-awake hold is held while displays
  are commanded dark and released on power-on, config change, bridge disable,
  client loss, crash-loop entry, and app exit (S10-13, S10-17A).
- Factory-reset/disable/settings edge cases: stale pairing code on disable,
  silent factory-reset failure, restart-marked settings not restarting the
  sidecar, silent config-write loss, config write races, external-change
  reconciliation (S10-17A).
- Pairing codes re-emitted after un-pairing, so the pairing window recovers
  without an app restart (S10-17C).

### Added
- **Tray auto-update (S10-11)**: check GitHub releases from the tray (plus a
  daily background check), consent-gated download, SHA-256 verification
  against the release manifest, silent Inno handoff or portable folder swap
  with relaunch-on-failure. Hardened by an adversarial security review
  (hash re-verified at execution time, redirect token-stripping pinned by
  test, size caps, cancellation cleanup, install-mode detection matched to
  the running executable) (S10-14, S10-17B).
- **Advertisement health (S10-12)**: the bridge probes its own mDNS
  advertisement (IPv4 + IPv6) and reports it over a new matterStatus frame
  (protocol v3, additive); surfaced in the pairing window and tray states
  (S10-15, S10-17C).
- Pairing window: shows the active VID/PID (must match a Google Home
  Developer Console integration) and the per-install identity-seed note;
  recenters on stage changes; flips to Paired live and auto-closes (S10-10,
  S10-10b, S10-10c).
- Blue tray state "running, not paired yet" for an enabled, uncommissioned
  bridge (ADR-011).
- "screensaver" power action with symmetric on/off toggle semantics (S10-13).

### Changed
- Overlay header is the static product name; the command row carries the
  command identity (incl. custom commands) with width-stable ellipsis
  (S10-16, S10-17B).
- Settings: identity seed value right-aligned with the other Advanced fields.
- User guide: troubleshooting rewritten from tonight's real-world failure
  modes — band isolation (phone on 2.4 GHz vs PC on 5 GHz), Console VID/PID
  mismatch presenting as generic can't-connect, hub reboot after Console
  changes, corrected red/gray icon states, updating section.

## [0.4.1] — 2026-08-25

Getting started, and getting started again. A first-run guide walks a fresh
install through registration, naming, and pairing; the pairing window now
names the real Home-app path with numbered steps and a live status line; and
re-pairing after a factory reset works instead of failing with "can't find
device". The bridge itself is nameable, so several PCs in one home are
tellable apart.

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
