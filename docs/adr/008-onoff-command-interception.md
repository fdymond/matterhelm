# ADR-008: dispatch plug actions from OnOff commands, not attribute changes

- **Status**: accepted
- **Date**: 2026-08-16
- **Story**: S8-1 (owner-directed: "is there any alternative to using on/off
  switch logic to trigger functionality?")

## Context

Every triggered action in the product rode a single primitive: an OnOff
**attribute-change event** (`matter/bridge.ts` subscribed `onOff$Changed` via
the adapter's `PlugHandle`). matter.js emits **no** change event when a write
lands on the value the attribute already holds — its own module doc says so and
`bridge.ts` relied on it for echo suppression. Three consequences, all
verifiable in the code as it stood:

1. A repeated identical command inside the momentary reset window was silently
   dropped: "Hey Google, turn on HTPC Next" twice in a row fired once, because
   the attribute was still `true` from the first press.
2. "Turn off HTPC Power" when the endpoint already read `off` did nothing at
   all — the stateful plug had no reset window to re-arm it.
3. The 300 ms auto-reset (S7-1, `HTPC_BRIDGE_MOMENTARY_RESET_MS`) was therefore
   **load-bearing for correctness** rather than presentation, and every press
   cost two subscription reports (the `on`, then our reset `off`).

Matter's `onOff` attribute is **read-only**: a controller cannot write it, only
invoke `On`/`Off`/`Toggle`. Commands are therefore a strictly more complete
observation point than attribute changes — nothing that reaches the attribute
from outside can bypass them. matter.js 0.17.7 supports this directly:
`OnOffPlugInUnitRequirements.OnOffServer` is subclassable, and `toggle()`,
`offWithEffect()`, `onWithRecallGlobalScene()` and `onWithTimedOff()` all
delegate to `on()`/`off()` in the default implementation, so two overrides
cover every command form.

Alternatives considered and rejected, in the same pass:

- **Generic Switch endpoints** (Matter's real momentary button). The direction
  is inverted — it is a device→controller *event source*, so it cannot receive
  commands, and Google grants it routine-starter grammar only, with no voice
  target and no tile control (ADR-002). Additive at best, never a replacement.
- **Media Playback / Casting Video Player clusters.** Semantically correct and
  supported by matter.js, but Google does not surface them (re-verified
  2026-08-02, `docs/routines.md`). Stays in the icebox pending Google release
  notes.
- **Both-edge dispatch with no reset** (every OnOff flip fires the action).
  Halves the traffic but forces the user to alternate "turn on"/"turn off" by
  voice, which fails goal G1.

## Decision

Dispatch On/Off **plug** actions from the command invocation.

- `matter/adapter.ts` mounts a `CommandObservingOnOffServer` — a subclass of
  the device type's own feature-specialized (`Lighting`) OnOff server, so
  cluster features and conformance are unchanged — overriding `on()`/`off()`
  to notify an observer registered per Matter endpoint id. matter.js types stay
  behind the adapter; the observer contract is plain data.
- `matter/bridge.ts` wires every plug (the momentary transport buttons, every
  ADR-004 custom command, and the stateful power switch) to that observer via
  `makePlugCommandHandler`, and **deletes** the attribute-change dispatch path
  for plugs — keeping both would double-fire each press. The reset scheduler is
  armed and cancelled by the command too.
- The auto-reset window is now **presentation only** (it returns the Home app
  tile to `off` so a press looks like a press). Its default stays 300 ms and it
  stays configurable; dispatch no longer depends on it.
- The **Speaker endpoint is deliberately excluded**. Its state is written
  locally by the tray app and read back, which is exactly what the
  `EchoSuppressor` exists for; attribute observation is the right model there,
  and LevelControl's several command forms (`MoveToLevel`, `Move`, `Step`, and
  the `WithOnOff` variants) make the attribute the simpler, more robust seam.
- **No protocol change.** `ClusterWrite`, the IPC frames, and
  `app/MatterHelm/Sidecar/Protocol.cs` are untouched; what changed is where the
  observation comes from, not what the rest of the pipeline sees. Endpoint
  identity, ids, VID/PID and the `uniqueId` seed are byte-identical, so no
  re-pairing.

This amends BLUEPRINT §2.2's "Momentary semantics" paragraph.

## Consequences

- **Easier**: repeated identical commands work — by voice, by routine, and by
  app tile. The reset window becomes a UX knob rather than a correctness
  dependency, so it can be tuned freely. One subscription report per press
  instead of two. Local reset writes invoke no command, so they cannot echo
  back as a press — no suppression logic needed on the plug path.
- **Harder**: the product now depends on a matter.js *behavior override*, a
  deeper API surface than the public event subscriptions it replaces. If
  matter.js changes the `on()`/`off()` delegation contract, transport silently
  stops dispatching. The boot smoke script (`src/matter/smoke.ts`) therefore
  asserts the double-`On` case against a live node, and that script is the
  rollback tripwire — reverting to `onOff$Changed` subscriptions restores the
  old behaviour with no storage, protocol, or pairing impact.
- **Watch (hardware)**: Google's Home client may optimistically suppress
  *sending* a redundant `On` when it believes the device is already on. In that
  case this fix covers app-tile taps, routines and post-reset repeats, but
  back-to-back identical voice phrases inside the window would still be lost —
  Google-side, not bridge-tunable. Only real hardware can decide this; rows are
  in `docs/e2e-log.md`.
