# ADR-013: focused-first media routing and retained mouse movement

- **Status**: accepted, amended by S11-3
- **Date**: 2026-08-30
- **Story**: S11-2 (owner-directed), S11-3 release remediation

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
- When focused delivery fails (including no foreground window), the captured
  current session is the intended fallback even if it belongs to another app.
  Pin the action to its captured `SourceAppUserModelId`; an owner change before
  or after the action fails verification instead of redirecting or proving it.
- Compute Play/Pause's desired state before delivery and use absolute
  `TryPlayAsync`/`TryPauseAsync` for every SMTC fallback. Never use a session
  toggle after sending a focused toggle.
- Cover the complete route with one three-second cancellation deadline. This
  stays below the IPC serial worker's five-second shutdown drain budget while
  retaining two 400 ms observation windows and bounded WinRT calls.
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
  keys that are deleted, disabled, or no longer mouse actions. Reject
  `mouseMove` inside a macro at both parse and settings-validation boundaries:
  a macro step has no independent retained edge.
- A mouse action cannot opt into automatic reset because the bridge's local
  reset deliberately emits no custom frame and therefore cannot restore the
  pointer.

## Consequences

- Kodi continues receiving focused Play/Pause even though it cannot be
  verified through SMTC. The log, user guide, and success UI are explicit that
  acknowledgement means delivery, not observed playback change.
- Chrome or another background session can no longer suppress or verify a Kodi
  command. It remains reachable when focused delivery genuinely fails.
- The two-second guard makes immediate repeated dedicated verbs idempotent for
  unverifiable targets. Residual limitation: Kodi Pause can still resume
  playback when the same dedicated Pause is sent again after the window, after
  restart, or from another foreground process identity.
- Pointer restore state remains in memory and does not survive app exit or
  crash. This is safe because the cursor remains visible; system-wide cursor
  blanking stays outside this decision and remains open in P-8.
- Protocol v4 peers are rejected coherently by the existing exact-version
  checks. Both executables ship together, so no mixed-version compatibility
  mode is added.
- Rollback requires both protocol peers, BLUEPRINT §2.3, and retained-mouse
  routing to move together; paired Matter endpoint identities do not change.
