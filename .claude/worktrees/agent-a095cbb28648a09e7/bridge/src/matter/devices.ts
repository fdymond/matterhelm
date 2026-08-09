/**
 * Endpoint specs for the §2.2 device model as amended by ADR-004 (settings +
 * custom commands): the endpoint set is now DERIVED from configuration —
 * disabled built-ins are omitted entirely, and each custom command becomes an
 * additional momentary On/Off plug.
 *
 * Pure module: derives each endpoint's stable identity (Matter endpoint id,
 * serialNumber, uniqueId) from the bridge's `uniqueIdSeed` and maps the
 * user-configurable display names (the Google voice targets) onto the
 * derived endpoint list. No runtime matter.js dependency — the ./adapter.js
 * import below is type-only and fully erased — so this module unit-tests
 * without loading matter.js.
 *
 * Identity rules:
 * - The Matter endpoint id and the serial/unique ids derive from the seed
 *   and the endpoint's ROLE — a built-in's {@link BuiltinEndpointKey}, or
 *   `custom-<key>` for a custom command — never from its display name:
 *   renaming a device must not change its identity, or Google Home would
 *   treat it as a brand-new device (ADR-004: keys are stable across renames).
 *   The `custom-` prefix keeps a custom key that happens to spell a built-in
 *   role (e.g. `next`) from colliding with that built-in's identity.
 * - `uniqueId` must differ from `serialNumber` on every endpoint (matter.js
 *   warns otherwise — BLUEPRINT §2.1 facts block); guaranteed here by
 *   disjoint derivation labels and different formats.
 * - Both fit Matter's 32-character field limits by construction: the
 *   serialNumber's human-readable role segment is truncated to 18 chars
 *   (uniqueness still holds — the hex digest covers the full role).
 */
import { createHash } from "node:crypto";

import type { BridgedDeviceInfo } from "./adapter.js";

/** Built-in endpoint roles, named exactly as `mapping/actions.ts`'s writes. */
export type BuiltinEndpointKey = "speaker" | "playPause" | "next" | "previous" | "power";

/** §2.2 table order — endpoint numbers are assigned in add order. */
export const BUILTIN_ENDPOINT_KEYS: readonly BuiltinEndpointKey[] = [
  "speaker",
  "playPause",
  "next",
  "previous",
  "power",
];

/** The three built-in auto-resetting transport buttons (BLUEPRINT §2.2). */
export type MomentaryEndpointKey = "playPause" | "next" | "previous";

export const MOMENTARY_ENDPOINT_KEYS: readonly MomentaryEndpointKey[] = [
  "playPause",
  "next",
  "previous",
];

/** Which adapter factory builds the endpoint (§2.2 device-type column). */
export type BridgedDeviceKind = "speaker" | "onOffPlug";

/** One built-in command's endpoint settings (ADR-004 §2). */
export interface BuiltinEndpointConfig {
  /** Display name — the Google voice target, user-configurable. */
  name: string;
  /** `false` = the endpoint is omitted from the bridge entirely. */
  enabled: boolean;
}

/**
 * One custom command (ADR-004 §2). Only enabled customs ever reach the
 * sidecar — the tray app omits disabled ones from the env, so there is no
 * `enabled` flag here.
 */
export interface CustomEndpointConfig {
  /** Kebab-case slug: wire identifier + endpoint identity. Never renamed. */
  key: string;
  /** Display name — the Google voice target. */
  name: string;
}

/** The parsed `HTPC_BRIDGE_ENDPOINTS` shape (ADR-004 §2), fully defaulted. */
export interface EndpointsConfig {
  speaker: BuiltinEndpointConfig;
  playPause: BuiltinEndpointConfig;
  next: BuiltinEndpointConfig;
  previous: BuiltinEndpointConfig;
  power: BuiltinEndpointConfig;
  custom: readonly CustomEndpointConfig[];
}

/**
 * One row of the derived endpoint table, ready for the adapter's endpoint
 * factories. Discriminated on `role`: built-ins carry their fixed §2.2 role,
 * custom commands carry `role: "custom"` plus their slug.
 */
export type EndpointSpec =
  | { role: BuiltinEndpointKey; kind: BridgedDeviceKind; info: BridgedDeviceInfo }
  | { role: "custom"; key: string; kind: "onOffPlug"; info: BridgedDeviceInfo };

/** True when the endpoint auto-resets to `off` after `on` (§2.2/ADR-004). */
export function isMomentary(spec: EndpointSpec): boolean {
  return (
    spec.role === "custom" || (MOMENTARY_ENDPOINT_KEYS as readonly string[]).includes(spec.role)
  );
}

/** Root-node identity (the bridge itself, not a bridged endpoint). */
export interface BridgeIdentity {
  serialNumber: string;
  uniqueId: string;
}

/**
 * Longest role segment embedded in a serialNumber: `htpc-` (5) + 18 + `-`
 * (1) + 8 hex = 32 chars, Matter's field limit, even for a 64-char custom
 * key. Built-in roles are shorter than this, so their serials are unchanged
 * from pre-ADR-004 builds (identity stability across the upgrade).
 */
const SERIAL_ROLE_MAX_LENGTH = 18;

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
    // "htpc-<role>-" + 8 hex chars — human-scannable in logs. The role
    // segment is truncated (see SERIAL_ROLE_MAX_LENGTH); the digest is not.
    serialNumber: `htpc-${role.toLowerCase().slice(0, SERIAL_ROLE_MAX_LENGTH)}-${stableHex(seed, role, "serial", 8)}`,
    // Different label AND different format from serialNumber, so the two can
    // never collide (matter.js warns when they are equal).
    uniqueId: stableHex(seed, role, "unique", 16),
  };
}

/** Identity of the bridge's root node, derived like the endpoints'. */
export function bridgeIdentity(seed: string): BridgeIdentity {
  return identityFor(seed, "bridge");
}

/**
 * Derives the bridge's endpoint set from configuration (ADR-004): enabled
 * built-ins in §2.2 table order, then custom commands in config order — each
 * a momentary On/Off plug with endpoint id `custom-<key>`. Disabled
 * built-ins yield no spec at all (the endpoint is never constructed).
 */
export function endpointSpecs(config: EndpointsConfig, seed: string): readonly EndpointSpec[] {
  const specs: EndpointSpec[] = [];
  for (const role of BUILTIN_ENDPOINT_KEYS) {
    if (!config[role].enabled) {
      continue;
    }
    specs.push({
      role,
      kind: role === "speaker" ? "speaker" : "onOffPlug",
      info: {
        // Fixed, role-derived Matter endpoint id (the matter.js storage key).
        id: role.toLowerCase(),
        name: config[role].name,
        ...identityFor(seed, role),
      },
    });
  }
  for (const custom of config.custom) {
    // `custom-<key>` is both the endpoint id and the identity role: stable
    // across renames, and disjoint from every built-in role by prefix.
    const role = `custom-${custom.key}`;
    specs.push({
      role: "custom",
      key: custom.key,
      kind: "onOffPlug",
      info: {
        id: role,
        name: custom.name,
        ...identityFor(seed, role),
      },
    });
  }
  return specs;
}
