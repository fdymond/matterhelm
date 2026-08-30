# ADR-008: dispatch plug actions from OnOff commands, not attribute changes

- **Status**: accepted; momentary/reset semantics superseded by ADR-012
- **Date**: 2026-08-16
- **Story**: S8-1 (owner-directed: "is there any alternative to using on/off
  switch logic to trigger functionality?")

The command-interception decision remains binding. ADR-012 explicitly
supersedes this ADR's former all-momentary reset scheduler, its 300-ms default,
and the S8-4 rule that all built-ins/custom endpoints share identical both-edge
semantics.

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
- **Both-edge dispatch with no reset** was rejected at the time. ADR-012 later
  adopted it for Next, Previous, and retained custom commands after controller
  echo evidence showed state retention was safer than timed reset. Play/Pause
  instead gives the two edges distinct meanings.

## Decision

Dispatch On/Off **plug** actions from the command invocation.

- `matter/adapter.ts` mounts a `CommandObservingOnOffServer` — a subclass of
  the device type's own feature-specialized (`Lighting`) OnOff server, so
  cluster features and conformance are unchanged — overriding `on()`/`off()`
  to notify an observer registered per Matter endpoint id. matter.js types stay
  behind the adapter; the observer contract is plain data.
- `matter/bridge.ts` wires every plug to that observer and **deletes** the
  attribute-change dispatch path for plugs — keeping both would double-fire.
  Current per-endpoint mapping is defined by ADR-012: retained Play/Pause,
  Next/Previous, reversible Power and default custom commands; next-tick-reset
  irreversible Power; configurable reset only for opted-in custom commands.
- The global reset window now applies only to custom commands whose
  `resetAfterActivation` is true. Its default is 0 ms (next tick), and dispatch
  never depends on the reset.
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

This command-source decision is incorporated in BLUEPRINT §2.2. ADR-012 is
the authority for endpoint state/reset semantics.

## Consequences

- **Easier**: repeated identical commands reach the command observer. Local
  reset writes invoke no command, so they cannot echo back as a press; no
  suppression logic is needed on the plug path.
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

## Superseded amendment 2026-08-16 (S8-4): momentary endpoints dispatched on Off too

The watch item resolved on real hardware the same day, in a sharper form than
predicted: the Home app's tile is a **toggle over Google's own state model**,
and that model lags or ignores the bridge's instant auto-reset. With the tile
stuck showing "on", the next tap arrives as an `Off` command — which the
mapping dropped as a non-action, so every other tap was dead and the user had
to toggle off manually before the next press worked (owner-reported;
re-typing the device to "Switch" in the Home app changes the icon only, not
the toggle semantics).

Historical decision: every then-momentary built-in/custom endpoint dispatched
both On and Off. ADR-012 supersedes that shared policy. Current behavior is:
Play/Pause maps On to Play and Off to Pause; Next/Previous and retained custom
commands dispatch on both edges; reset-enabled custom commands dispatch only
On; reversible Power maps each edge to its direction; irreversible Power
dispatches only Off. The core safety fact remains: local attribute writes never
reach the command observer.
