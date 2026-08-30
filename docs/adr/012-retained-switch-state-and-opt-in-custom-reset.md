# ADR-012: retain switch state with explicit momentary exceptions

- **Status**: accepted; media route amended by ADR-013
- **Date**: 2026-08-30
- **Story**: S10-30, amended by S10-32 (owner-directed behaviour changes)

## Context

The auto-reset model in ADR-008/S8-4 made Google Home controller echoes
observable as a second user action: an On activation ran a toggle action, then
the reset/report cycle could produce an Off command that ran the toggle again.
For Play/Pause this meant playback started and then paused a few seconds later.
The attempted S10-29 temporal suppression of a trailing Off was reverted by
owner decision because timing cannot reliably distinguish controller intent
from an echo. Matter OnOff endpoints already carry the state needed to express
the behavior without a suppression window. Windows appcommands labelled Play
toggle in measured Spotify and YouTube sessions, so any SMTC fallback must use
an absolute verb. ADR-013 later added an ownership-aware focused appcommand
attempt for focus-driven players while preserving absolute session fallback.

## Decision

Amend BLUEPRINT §§2.2–2.3 and supersede only the momentary/reset portions of
ADR-008 and its S8-4 amendment. ADR-008's command-interception decision remains
binding.

- Play/Pause, Next, and Previous always retain their Matter OnOff state and
  schedule no automatic write. Power retains state only for reversible actions.
- Play/Pause is a true state switch: On emits the `play` action introduced in
  protocol v4 (carried in the current v5 frame), and Off emits `pause`. The
  existing `playPause` action remains valid
  for custom media-key actions and older internal flows.
- Next and Previous execute their respective action once on either controller
  transition.
- Power models "is the PC awake." Reversible modes (`displaysOff`,
  `pauseAndDisplaysOff`, `screensaver`) are stateful: Off engages the action and
  On wakes the displays or stops the screensaver. `pauseAndDisplaysOff` On
  reverses only the display half and never resumes playback.
- Irreversible Power modes (currently `sleep`) are momentary: only controller
  Off dispatches the action. After dispatch is queued, the sidecar schedules a
  fixed next-tick local write to On so the update has the best chance to flush
  before suspension. The local attribute write invokes no OnOff command, so it
  dispatches nothing; controller On also dispatches nothing.
- The tray derives Power's `momentary` flag from `powerOffAction`, includes it
  in the Power entry of `HTPC_BRIDGE_ENDPOINTS`, and restarts the sidecar when
  the configured action changes. Missing env fields default to `false`.
- A custom command defaults to the same both-edge retained-state behavior. Its
  additive `resetAfterActivation` boolean may opt it into momentary behavior.
  In that mode only On executes; after `momentaryResetMs`, the bridge writes the
  attribute Off locally. That local attribute write invokes no OnOff command,
  so it executes nothing and needs no echo suppression.
- The tray includes `resetAfterActivation` in `config.json` and in each enabled
  custom entry of `HTPC_BRIDGE_ENDPOINTS`. Missing config/env fields default to
  `false`.
- IPC message revision `v` increases from 3 to 4 for the additive `play` and
  `pause` variants. `hello.protocol` remains 1 because no existing frame or
  field changes meaning or shape.

## Consequences

- One controller transition produces at most one action, while endpoint state
  remains honest and visible to Google Home.
- Play/Pause no longer depends on a toggle key, so an On/Off echo cannot invert
  playback a second time.
- Next, Previous, and non-resetting custom commands intentionally execute on
  both transition directions; routines that deliberately send On then Off
  therefore execute twice.
- Users who need button-like custom behavior must opt in per command. The
  global reset delay remains relevant only to those opted-in custom commands.
- Irreversible Power reset timing is intentionally fixed at the next tick and
  is unaffected by the custom-command reset delay.
- No timing-based trailing-Off suppression is introduced. Rolling back this
  decision means restoring auto-reset wiring and protocol-v3 transport mapping;
  Matter endpoint identities and pairing storage are unchanged.
