# ADR-013: focused-first media routing and retained mouse movement

- **Status**: accepted, amended by S11-3, S11-5, S11-6, and S11-8
- **Date**: 2026-08-30
- **Story**: S11-2 (owner-directed), S11-3 release remediation, S11-5 macro mouse steps, S11-6 screensaver focus restoration, S11-8 field remediation

## Context

BLUEPRINT §2.2 and ADR-012 originally required dedicated Play/Pause to use
only the current Windows media session. That protected absolute intent, but
focus-driven players such as Kodi publish no session and received nothing.
S11-2 added a focused `WM_APPCOMMAND` attempt and a retained mouse action.

Release review found two missing identities in that design. Windows exposes a
global current session, not necessarily the focused app's session, and the
custom IPC frame carried only a key, not the On/Off edge. A stale paused Chrome
session therefore suppressed Pause while focused Kodi kept playing, and the
tray had to guess mouse edges. Measurements also established that focused Kodi
obeys `APPCOMMAND_MEDIA_PLAY` absolutely but treats `APPCOMMAND_MEDIA_PAUSE` as
a toggle, while Kodi publishes no SMTC session.

## Decision

Amend BLUEPRINT §§2.2–2.4 and supersede ADR-012/ADR-003 only where they require
SMTC-only media routing or a key-only custom frame.

- Capture the foreground HWND and resolve its owning PID, executable name, and
  application user model id when available. Every SMTC observation includes
  its `SourceAppUserModelId`. Log the ownership comparison at the initial and
  post-focused observation points.
- Short-circuit an absolute verb, verify focused delivery, or use an
  unhandled-delivery fallback only when the session belongs to the captured
  foreground app. A different or unresolved owner is unverifiable: it cannot
  suppress delivery, prove success, or receive fallback after a successfully
  delivered focused command.
- When delivery was aimed at a specific foreground window and fails, only a
  captured session owned by that same foreground app is an intended fallback.
  A different-owner session is never actioned. When no foreground target could
  be captured, the captured current session remains the intended fallback.
  Pin every fallback to its captured `SourceAppUserModelId`; an owner change
  before or after the action fails verification instead of redirecting or
  proving it.
- Treat Win32 `ERROR_TIMEOUT` from the focused `WM_APPCOMMAND` as a transient
  just-restored-window condition: wait 200 ms and retry once with another
  one-second `SendMessageTimeoutW`. Do not retry other delivery failures. Cover
  the route with a four-second cancellation deadline; the worst synchronous
  timeout path remains inside the IPC worker's five-second shutdown drain.
- Compute Play/Pause's desired state before delivery and use absolute
  `TryPlayAsync`/`TryPauseAsync` for every SMTC fallback. Never use a session
  toggle after sending a focused toggle.
- Bound session reads/actions to one second and retain the 400 ms observation
  interval. Together with the appcommand retry policy, the complete route
  remains bounded by the four-second cancellation deadline described above.
- A delivered focused command without a matching session is an acknowledged,
  explicitly unverifiable delivery. It does not claim playback changed. To
  limit Kodi's toggle-like Pause hazard without dropping Kodi support, suppress
  an identical dedicated Play or Pause repeated to the same unverifiable
  process within two seconds and log the suppression. Two seconds covers
  immediate Matter/routine duplicates; later deliberate commands still pass.
- Keep mouse movement retained. Bump additive message revision 4 → 5 (handshake
  protocol remains 1) and carry `{name:"custom", key, on}` field-for-field on
  both IPC peers. On captures/moves; Off restores/removes. Off before On is a
  successful no-op, and repeated On replaces the next restore position.
- Reconcile retained mouse captures whenever configuration changes, removing
  keys that are deleted, disabled, or no longer mouse actions.
- Permit `mouseMove` inside a macro with deliberately different semantics: a
  sequence step has no independent On/Off edge, so it performs one absolute
  move to its configured target and nothing else. It never reads or writes the
  retained capture dictionary. There is no implicit restore or hidden state;
  a sequence that needs a later position adds another explicit mouse step.
- Use the standalone action's target presets and custom-coordinate resolver for
  sequence steps, including virtual-desktop clamping. Windows clamps rather
  than parks off-screen (measured `(5000,5000)` → `(1600,1000)` and
  `(-500,-500)` → `(0,0)` on a 1600×1000 desktop), so corner parking is the
  available behavior. Do not add cursor hiding; P-8 is closed will-not-implement.
- A mouse action cannot opt into automatic reset because the bridge's local
  reset deliberately emits no custom frame and therefore cannot restore the
  pointer.
- Immediately before either the Power route or a custom command starts the
  screensaver, capture the foreground HWND plus its PID, process name, and
  window title in process memory. A second start replaces the first capture.
- After either MatterHelm stop route closes the screensaver, consume the
  capture and validate that the HWND still exists and still belongs to the
  captured PID and process name. Restore a minimized window, try
  `SetForegroundWindow`, then retry while the action thread is attached to the
  current foreground thread's input queue. If activation remains unavailable,
  revalidate identity and retry every 100 ms for up to 1.5 seconds. Log a
  titled INFO outcome on success and one reasoned WARN after stale identity or
  final Windows refusal.
- Amend S11-6's action contract: successfully closing the screensaver (or
  finding none running) is success even if focus restoration is refused.
  Restoration is a best-effort enhancement and its failure cannot nack Power
  On or abort a command sequence.
- Clear screensaver focus state after every restore attempt, on bridge
  disable, and on app exit. Do not persist window handles across sessions.
  Display-off routes have no focus side effect and therefore own no such state.

## Consequences

- Kodi continues receiving focused Play/Pause even though it cannot be
  verified through SMTC. The log, user guide, and success UI are explicit that
  acknowledgement means delivery, not observed playback change.
- Chrome, Spotify, or another background session can no longer suppress,
  verify, or receive fallback for a Kodi-targeted command. A failed Kodi
  delivery succeeds only through a Kodi-owned session; otherwise it fails with
  the target/fallback reason.
- The two-second guard makes immediate repeated dedicated verbs idempotent for
  unverifiable targets. Residual limitation: Kodi Pause can still resume
  playback when the same dedicated Pause is sent again after the window, after
  restart, or from another foreground process identity.
- Pointer restore state remains in memory and does not survive app exit or
  crash. Macro mouse steps cannot contaminate it because they use a separate
  stateless executor payload. The cursor remains visible; system-wide cursor
  blanking is rejected and P-8 is closed will-not-implement.
- S11-5 is app-local: protocol v5 already carries the outer custom command
  edge, while macro steps exist only in the tray configuration and executor.
  No sidecar or Matter protocol change is required.
- Protocol v4 peers are rejected coherently by the existing exact-version
  checks. Both executables ship together, so no mixed-version compatibility
  mode is added.
- Rollback requires both protocol peers, BLUEPRINT §2.3, and retained-mouse
  routing to move together; paired Matter endpoint identities do not change.
- S11-6 is also app-local and requires no sidecar or IPC revision: Power and
  custom screensaver commands already converge on the same executor verbs.
  A user-driven mouse/keyboard dismissal bypasses those stop verbs, so focus
  cannot be restored in that case; the user guide states this limitation.
- S11-8 is app-local and requires no sidecar or IPC revision. Macro errors now
  carry executor diagnostics rather than reusing success-pill copy, so logs and
  failed acknowledgements describe the actual failed operation.
