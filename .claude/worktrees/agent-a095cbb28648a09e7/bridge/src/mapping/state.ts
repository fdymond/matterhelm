/**
 * Pure mapping: sidecar `state` frames -> Matter cluster attribute updates.
 *
 * docs/BLUEPRINT.md §2.2 is the normative device model; this module is its
 * executable form for the "tray app reported real device state" -> "update
 * the Speaker endpoint's attributes" direction — the inverse of
 * `mapping/actions.ts`. No matter.js types appear here; the caller
 * (`matter/bridge.ts`) applies the returned plain values to the real
 * behavior/attribute state.
 */
import type { StateFrame } from "../ipc/protocol.js";

/** Plain descriptor of the Speaker endpoint's attributes to apply. */
export interface SpeakerAttributeUpdate {
  /** LevelControl `currentLevel` attribute, 0-254. */
  currentLevel: number;
  /** OnOff attribute; see {@link mutedToOnOff} for the polarity. */
  onOff: boolean;
}

/**
 * Converts the protocol's volume percent (0-100) to a Matter LevelControl
 * `currentLevel` (0-254).
 *
 * Rounding rule: `Math.round((volume / 100) * 254)`, i.e. proportional
 * scaling then round-to-nearest — the exact inverse of
 * `mapping/actions.ts`'s `levelToVolume`: for every integer percent 0-100,
 * `volumeToLevel` then `levelToVolume` returns the original percent (see the
 * exhaustive round-trip test in state.test.ts). Input is clamped to 0-100
 * first: `ipc/protocol.ts`'s `StateFrameSchema` already restricts `volume`
 * to that range, but clamping here means this function has no invalid
 * input it can silently misbehave on.
 */
export function volumeToLevel(volume: number): number {
  const clamped = Math.min(100, Math.max(0, volume));
  return Math.round((clamped / 100) * 254);
}

/**
 * Speaker OnOff cluster polarity (BLUEPRINT §2.2: "OnOff (= mute)").
 * Exact inverse of `mapping/actions.ts`'s `onOffToMuted`: **muted -> Off**,
 * **unmuted -> On**.
 */
export function mutedToOnOff(muted: boolean): boolean {
  return !muted;
}

/**
 * Converts a reported `state` frame (volume/muted, as sent by the tray app)
 * into the Speaker endpoint's cluster attribute values.
 */
export function stateFrameToSpeakerAttributes(
  frame: Pick<StateFrame, "volume" | "muted">,
): SpeakerAttributeUpdate {
  return {
    currentLevel: volumeToLevel(frame.volume),
    onOff: mutedToOnOff(frame.muted),
  };
}
