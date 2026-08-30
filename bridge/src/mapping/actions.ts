/**
 * Pure mapping: Matter-side cluster writes -> sidecar `action` frames.
 *
 * docs/BLUEPRINT.md §2.2 is the normative device model; this module is its
 * executable form for the "cluster write happened" -> "tell the tray app"
 * direction. No matter.js types appear here (they stay behind
 * `matter/adapter.ts` per the architecture invariant) — inputs are plain,
 * serializable descriptors of what a cluster write meant, decided by the
 * caller (`matter/bridge.ts`) from the real matter.js behavior/attribute
 * event.
 *
 * Pure = no I/O, no randomness: the `action` frame's `id` (a uuid, per
 * ipc/protocol.ts) is supplied by the caller rather than generated here.
 */
import { PROTOCOL_VERSION, type ActionFrame } from "../ipc/protocol.js";

/**
 * A single cluster write observed on an endpoint, as a plain descriptor.
 * Mirrors docs/BLUEPRINT.md §2.2's endpoint table plus ADR-004's custom
 * commands:
 * - `speaker` / `onOff`: the Speaker endpoint's OnOff cluster, used as mute
 *   control (not power) — see {@link onOffToMuted} for the polarity.
 * - `speaker` / `levelControl`: the Speaker endpoint's LevelControl
 *   `currentLevel` attribute, 0-254.
 * - `playPause` | `next` | `previous`: retained-state On/Off Plug-in Unit
 *   endpoints; `on` is the OnOff command the controller invoked (ADR-008).
 * - `power`: the On/Off Plug-in Unit endpoint; `momentary` is true for an
 *   irreversible configured action whose Off command resets locally to On.
 * - `custom`: a user-defined On/Off plug; `key` is the command's slug and
 *   `resetAfterActivation` selects its opt-in momentary mode (ADR-012).
 */
export type ClusterWrite =
  | { endpoint: "speaker"; cluster: "onOff"; on: boolean }
  | { endpoint: "speaker"; cluster: "levelControl"; level: number }
  | { endpoint: "playPause" | "next" | "previous"; cluster: "onOff"; on: boolean }
  | { endpoint: "power"; cluster: "onOff"; on: boolean; momentary: boolean }
  | {
      endpoint: "custom";
      key: string;
      cluster: "onOff";
      on: boolean;
      resetAfterActivation: boolean;
    };

/**
 * Converts a Matter LevelControl `currentLevel` (0-254) to the protocol's
 * volume percent (0-100).
 *
 * Rounding rule: `Math.round((level / 254) * 100)`, i.e. proportional
 * scaling then round-to-nearest. This is the exact inverse of
 * {@link import("./state.js").volumeToLevel} — for every integer percent
 * 0-100, `volumeToLevel` then `levelToVolume` returns the original percent
 * (see state.test.ts's exhaustive round-trip test). Input is clamped to
 * 0-254 first: matter.js's own LevelControl schema already restricts the
 * attribute to a uint8, but clamping here means a boundary value from
 * matter.js (an external, uncontrolled input) can never produce an
 * out-of-range percent even under a library bug.
 */
export function levelToVolume(level: number): number {
  const clamped = Math.min(254, Math.max(0, level));
  return Math.round((clamped / 254) * 100);
}

/**
 * Speaker OnOff cluster polarity (BLUEPRINT §2.2: "OnOff (= mute)").
 * Chosen convention: **On = unmuted** (the speaker is "on", audio audible),
 * **Off = muted** (silenced) — the natural reading of a device's own
 * on/off switch, not an inversion of it. `state.ts`'s `mutedToOnOff` is the
 * exact inverse of this function.
 */
export function onOffToMuted(on: boolean): boolean {
  return !on;
}

/**
 * Converts one observed Matter cluster write into the `action` frame the
 * sidecar should send to the tray app.
 *
 * Play/Pause maps state to an absolute verb. Next/Previous and retained custom
 * switches dispatch on both user transitions. Momentary Power maps Off only;
 * reset-enabled custom maps On only. Their local attribute resets never reach
 * this command mapping.
 *
 * `id` is the frame's uuid, supplied by the caller (this function is pure).
 */
export function clusterWriteToAction(write: ClusterWrite, id: string): ActionFrame | null {
  const v = PROTOCOL_VERSION;
  switch (write.endpoint) {
    case "speaker": {
      if (write.cluster === "onOff") {
        return { v, type: "action", id, name: "setMuted", value: onOffToMuted(write.on) };
      }
      return { v, type: "action", id, name: "setVolume", value: levelToVolume(write.level) };
    }
    case "power": {
      if (write.momentary && write.on) {
        return null;
      }
      return { v, type: "action", id, name: write.on ? "powerOn" : "powerOff" };
    }
    case "playPause": {
      return { v, type: "action", id, name: write.on ? "play" : "pause" };
    }
    case "next":
    case "previous": {
      return { v, type: "action", id, name: write.endpoint };
    }
    case "custom": {
      if (write.resetAfterActivation && !write.on) {
        return null;
      }
      return { v, type: "action", id, name: "custom", key: write.key };
    }
  }
}
