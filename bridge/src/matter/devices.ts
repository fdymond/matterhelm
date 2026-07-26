/**
 * Endpoint specs for the §2.2 device model (docs/BLUEPRINT.md, normative).
 *
 * Pure module: derives each endpoint's stable identity (Matter endpoint id,
 * serialNumber, uniqueId) from the bridge's `uniqueIdSeed` and maps the
 * user-configurable display names (the Google voice targets) onto the §2.2
 * table. No runtime matter.js dependency — the ./adapter.js import below is
 * type-only and fully erased — so this module unit-tests without loading
 * matter.js.
 *
 * Identity rules:
 * - The Matter endpoint id and the serial/unique ids derive from the seed
 *   and the endpoint's ROLE (its {@link EndpointKey}), never from its display
 *   name: renaming a device must not change its identity, or Google Home
 *   would treat it as a brand-new device.
 * - `uniqueId` must differ from `serialNumber` on every endpoint (matter.js
 *   warns otherwise — BLUEPRINT §2.1 facts block); guaranteed here by
 *   disjoint derivation labels and different formats.
 * - Both fit Matter's 32-character field limits by construction.
 */
import { createHash } from "node:crypto";

import type { BridgedDeviceInfo } from "./adapter.js";

/** Endpoint roles, named exactly as `mapping/actions.ts`'s `ClusterWrite`. */
export type EndpointKey = "speaker" | "playPause" | "next" | "previous" | "power";

/** The three auto-resetting transport buttons (BLUEPRINT §2.2). */
export type MomentaryEndpointKey = "playPause" | "next" | "previous";

export const MOMENTARY_ENDPOINT_KEYS: readonly MomentaryEndpointKey[] = [
  "playPause",
  "next",
  "previous",
];

/** Which adapter factory builds the endpoint (§2.2 device-type column). */
export type BridgedDeviceKind = "speaker" | "onOffPlug";

/** Display names per endpoint — the Google voice targets, user-configurable. */
export interface DeviceNames {
  speaker: string;
  playPause: string;
  next: string;
  previous: string;
  power: string;
}

/** One row of the §2.2 table, ready for the adapter's endpoint factories. */
export interface EndpointSpec {
  key: EndpointKey;
  kind: BridgedDeviceKind;
  info: BridgedDeviceInfo;
}

/** All five rows, keyed by role. `bridge.ts` adds them in §2.2 table order. */
export type EndpointSpecs = Readonly<Record<EndpointKey, EndpointSpec>>;

/** Root-node identity (the bridge itself, not a bridged endpoint). */
export interface BridgeIdentity {
  serialNumber: string;
  uniqueId: string;
}

/** Deterministic hex digest of (seed, role, label) — the identity source. */
function stableHex(seed: string, role: string, label: string, length: number): string {
  return (
    createHash("sha256")
      // JSON-encoding the triple makes the digest input unambiguous for any seed.
      .update(JSON.stringify([seed, role, label]))
      .digest("hex")
      .slice(0, length)
  );
}

function identityFor(seed: string, role: string): BridgeIdentity {
  return {
    // "htpc-<role>-" (≤ 15 chars) + 8 hex chars — human-scannable in logs.
    serialNumber: `htpc-${role.toLowerCase()}-${stableHex(seed, role, "serial", 8)}`,
    // Different label AND different format from serialNumber, so the two can
    // never collide (matter.js warns when they are equal).
    uniqueId: stableHex(seed, role, "unique", 16),
  };
}

/** Identity of the bridge's root node, derived like the endpoints'. */
export function bridgeIdentity(seed: string): BridgeIdentity {
  return identityFor(seed, "bridge");
}

/** Builds the §2.2 endpoint table for the given names and identity seed. */
export function endpointSpecs(names: DeviceNames, seed: string): EndpointSpecs {
  const spec = (key: EndpointKey, kind: BridgedDeviceKind): EndpointSpec => ({
    key,
    kind,
    info: {
      // Fixed, role-derived Matter endpoint id (the matter.js storage key).
      id: key.toLowerCase(),
      name: names[key],
      ...identityFor(seed, key),
    },
  });
  return {
    speaker: spec("speaker", "speaker"),
    playPause: spec("playPause", "onOffPlug"),
    next: spec("next", "onOffPlug"),
    previous: spec("previous", "onOffPlug"),
    power: spec("power", "onOffPlug"),
  };
}
