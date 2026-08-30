/**
 * Specification tests for matter/devices.ts (docs/BLUEPRINT.md §2.2 table +
 * ADR-004 endpoint-set derivation + the identity rules in the module doc).
 * Pure — no matter.js loaded.
 */
import { describe, expect, it } from "vitest";

import {
  BUILTIN_ENDPOINT_KEYS,
  bridgeIdentity,
  endpointSpecs,
  isMomentary,
  type EndpointsConfig,
  type EndpointSpec,
} from "./devices.js";

const allEnabled: EndpointsConfig = {
  speaker: { name: "HTPC Speaker", enabled: true },
  playPause: { name: "HTPC Play Pause", enabled: true },
  next: { name: "HTPC Next", enabled: true },
  previous: { name: "HTPC Previous", enabled: true },
  power: { name: "HTPC Power", enabled: true, momentary: false },
  custom: [],
};

const seed = "test-seed";

function withCustom(
  ...custom: { key: string; name: string; resetAfterActivation?: boolean }[]
): EndpointsConfig {
  return {
    ...allEnabled,
    custom: custom.map((entry) => ({
      ...entry,
      resetAfterActivation: entry.resetAfterActivation ?? false,
    })),
  };
}

function roles(specs: readonly EndpointSpec[]): string[] {
  return specs.map((spec) => (spec.role === "custom" ? `custom:${spec.key}` : spec.role));
}

describe("endpointSpecs — endpoint-set derivation (ADR-004)", () => {
  it("derives all five §2.2 endpoints, in table order, when everything is enabled", () => {
    expect(roles(endpointSpecs(allEnabled, seed))).toEqual([...BUILTIN_ENDPOINT_KEYS]);
  });

  it("omits a disabled built-in entirely", () => {
    const config = { ...allEnabled, playPause: { name: "HTPC Play Pause", enabled: false } };
    expect(roles(endpointSpecs(config, seed))).toEqual(["speaker", "next", "previous", "power"]);
  });

  it("omits a disabled speaker (no speaker endpoint at all)", () => {
    const config = { ...allEnabled, speaker: { name: "HTPC Speaker", enabled: false } };
    const specs = endpointSpecs(config, seed);
    expect(specs.some((spec) => spec.kind === "speaker")).toBe(false);
    expect(roles(specs)).toEqual(["playPause", "next", "previous", "power"]);
  });

  it("derives an empty set when every built-in is disabled and no custom exists", () => {
    const config: EndpointsConfig = {
      speaker: { name: "S", enabled: false },
      playPause: { name: "P", enabled: false },
      next: { name: "N", enabled: false },
      previous: { name: "V", enabled: false },
      power: { name: "W", enabled: false, momentary: false },
      custom: [],
    };
    expect(endpointSpecs(config, seed)).toEqual([]);
  });

  it("appends custom commands after the built-ins, in config order", () => {
    const config = withCustom(
      { key: "movie-mode", name: "Movie Mode" },
      { key: "stop-media", name: "HTPC Stop" },
    );
    expect(roles(endpointSpecs(config, seed))).toEqual([
      ...BUILTIN_ENDPOINT_KEYS,
      "custom:movie-mode",
      "custom:stop-media",
    ]);
  });

  it("models the speaker as the one Speaker device and everything else as plugs", () => {
    const specs = endpointSpecs(withCustom({ key: "movie-mode", name: "Movie Mode" }), seed);
    for (const spec of specs) {
      expect(spec.kind).toBe(spec.role === "speaker" ? "speaker" : "onOffPlug");
    }
  });

  it("uses the configured display name as the endpoint name (voice target)", () => {
    const specs = endpointSpecs(withCustom({ key: "movie-mode", name: "Movie Mode" }), seed);
    const nameOf = (id: string): string | undefined =>
      specs.find((spec) => spec.info.id === id)?.info.name;
    expect(nameOf("speaker")).toBe("HTPC Speaker");
    expect(nameOf("power")).toBe("HTPC Power");
    expect(nameOf("custom-movie-mode")).toBe("Movie Mode");
  });
});

describe("isMomentary — custom opt-in auto-reset semantics", () => {
  const specs = endpointSpecs(
    withCustom(
      { key: "stateful", name: "Stateful" },
      { key: "movie-mode", name: "Movie Mode", resetAfterActivation: true },
    ),
    seed,
  );

  it("marks only reset-enabled custom commands as momentary in reversible Power mode", () => {
    const momentaryIds = specs.filter(isMomentary).map((spec) => spec.info.id);
    expect(momentaryIds).toEqual(["custom-movie-mode"]);
  });

  it("marks Power as momentary when its configured action is irreversible", () => {
    const momentaryPower = endpointSpecs(
      { ...allEnabled, power: { ...allEnabled.power, momentary: true } },
      seed,
    ).filter(isMomentary);
    expect(momentaryPower.map((spec) => spec.info.id)).toEqual(["power"]);
  });

  it("marks all built-ins and default custom commands as non-momentary", () => {
    const stateful = specs.filter((spec) => !isMomentary(spec)).map((spec) => spec.info.id);
    expect(stateful).toEqual([
      "speaker",
      "playpause",
      "next",
      "previous",
      "power",
      "custom-stateful",
    ]);
  });
});

describe("endpointSpecs — stable identity", () => {
  it("derives fixed lowercase endpoint ids from the role, not the name", () => {
    const ids = endpointSpecs(allEnabled, seed).map((spec) => spec.info.id);
    expect(ids).toEqual(["speaker", "playpause", "next", "previous", "power"]);
  });

  it("gives a custom command the endpoint id custom-<key>", () => {
    const specs = endpointSpecs(withCustom({ key: "movie-mode", name: "Movie Mode" }), seed);
    expect(specs.at(-1)?.info.id).toBe("custom-movie-mode");
  });

  it("is deterministic: the same seed always yields the same identity", () => {
    const config = withCustom({ key: "movie-mode", name: "Movie Mode" });
    expect(endpointSpecs(config, seed)).toEqual(endpointSpecs(config, seed));
  });

  it("keeps a built-in's identity unchanged when it is renamed", () => {
    const find = (specs: readonly EndpointSpec[]): EndpointSpec | undefined =>
      specs.find((spec) => spec.info.id === "playpause");
    const renamed = find(
      endpointSpecs({ ...allEnabled, playPause: { name: "Media Toggle", enabled: true } }, seed),
    );
    const original = find(endpointSpecs(allEnabled, seed));
    expect(renamed).toBeDefined();
    expect(renamed?.info.serialNumber).toBe(original?.info.serialNumber);
    expect(renamed?.info.uniqueId).toBe(original?.info.uniqueId);
  });

  it("keeps a custom command's identity unchanged when its display name changes (ADR-004)", () => {
    const before = endpointSpecs(withCustom({ key: "movie-mode", name: "Movie Mode" }), seed);
    const after = endpointSpecs(withCustom({ key: "movie-mode", name: "Cinema Time" }), seed);
    expect(after.at(-1)?.info.id).toBe(before.at(-1)?.info.id);
    expect(after.at(-1)?.info.serialNumber).toBe(before.at(-1)?.info.serialNumber);
    expect(after.at(-1)?.info.uniqueId).toBe(before.at(-1)?.info.uniqueId);
  });

  it("keeps built-in identities unchanged by the presence of custom commands", () => {
    const plain = endpointSpecs(allEnabled, seed);
    const withOne = endpointSpecs(withCustom({ key: "movie-mode", name: "Movie Mode" }), seed);
    for (let i = 0; i < plain.length; i += 1) {
      expect(withOne[i]).toEqual(plain[i]);
    }
  });

  it("changes every serialNumber and uniqueId when the seed changes", () => {
    const config = withCustom({ key: "movie-mode", name: "Movie Mode" });
    const original = endpointSpecs(config, seed);
    const other = endpointSpecs(config, "other-seed");
    for (let i = 0; i < original.length; i += 1) {
      expect(other[i]?.info.serialNumber).not.toBe(original[i]?.info.serialNumber);
      expect(other[i]?.info.uniqueId).not.toBe(original[i]?.info.uniqueId);
    }
  });

  it("gives every endpoint a distinct serialNumber and a distinct uniqueId", () => {
    const specs = endpointSpecs(
      withCustom({ key: "movie-mode", name: "Movie Mode" }, { key: "stop-media", name: "Stop" }),
      seed,
    );
    expect(new Set(specs.map((spec) => spec.info.serialNumber)).size).toBe(specs.length);
    expect(new Set(specs.map((spec) => spec.info.uniqueId)).size).toBe(specs.length);
  });

  it("keeps a custom key spelling a built-in role (e.g. 'next') fully distinct in identity", () => {
    const specs = endpointSpecs(withCustom({ key: "next", name: "Custom Next" }), seed);
    const builtinNext = specs.find((spec) => spec.info.id === "next");
    const customNext = specs.find((spec) => spec.info.id === "custom-next");
    expect(builtinNext).toBeDefined();
    expect(customNext).toBeDefined();
    expect(customNext?.info.serialNumber).not.toBe(builtinNext?.info.serialNumber);
    expect(customNext?.info.uniqueId).not.toBe(builtinNext?.info.uniqueId);
  });

  it("never reuses a serialNumber as a uniqueId (matter.js warns on equality)", () => {
    const specs = endpointSpecs(withCustom({ key: "movie-mode", name: "Movie Mode" }), seed);
    for (const spec of specs) {
      expect(spec.info.uniqueId).not.toBe(spec.info.serialNumber);
    }
  });

  it("fits Matter's 32-character limits even for a maximum-length custom key", () => {
    const longKey = `${"k".repeat(31)}-${"m".repeat(32)}`; // 64 chars, valid slug
    const specs = endpointSpecs(withCustom({ key: longKey, name: "Long" }), seed);
    for (const spec of specs) {
      expect(spec.info.serialNumber.length).toBeLessThanOrEqual(32);
      expect(spec.info.uniqueId.length).toBeLessThanOrEqual(32);
    }
  });

  it("keeps two long custom keys sharing a serial prefix distinct via the digest", () => {
    const base = "k".repeat(40);
    const specs = endpointSpecs(
      withCustom({ key: `${base}-a`, name: "A" }, { key: `${base}-b`, name: "B" }),
      seed,
    );
    const [a, b] = specs.slice(-2);
    expect(a?.info.serialNumber).not.toBe(b?.info.serialNumber);
    expect(a?.info.uniqueId).not.toBe(b?.info.uniqueId);
  });
});

describe("bridgeIdentity — root node identity", () => {
  it("is deterministic for the same seed", () => {
    expect(bridgeIdentity(seed)).toEqual(bridgeIdentity(seed));
  });

  it("differs from every bridged endpoint's identity", () => {
    const bridge = bridgeIdentity(seed);
    const specs = endpointSpecs(withCustom({ key: "bridge", name: "Sneaky" }), seed);
    for (const spec of specs) {
      expect(spec.info.serialNumber).not.toBe(bridge.serialNumber);
      expect(spec.info.uniqueId).not.toBe(bridge.uniqueId);
    }
  });

  it("keeps uniqueId distinct from serialNumber and within 32 characters", () => {
    const bridge = bridgeIdentity(seed);
    expect(bridge.uniqueId).not.toBe(bridge.serialNumber);
    expect(bridge.serialNumber.length).toBeLessThanOrEqual(32);
    expect(bridge.uniqueId.length).toBeLessThanOrEqual(32);
  });
});
