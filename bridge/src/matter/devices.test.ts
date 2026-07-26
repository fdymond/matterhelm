/**
 * Specification tests for matter/devices.ts (docs/BLUEPRINT.md §2.2 table +
 * the identity rules in the module doc). Pure — no matter.js loaded.
 */
import { describe, expect, it } from "vitest";

import {
  MOMENTARY_ENDPOINT_KEYS,
  bridgeIdentity,
  endpointSpecs,
  type DeviceNames,
  type EndpointKey,
  type EndpointSpec,
} from "./devices.js";

const names: DeviceNames = {
  speaker: "HTPC Speaker",
  playPause: "HTPC Play Pause",
  next: "HTPC Next",
  previous: "HTPC Previous",
  power: "HTPC Power",
};

const seed = "test-seed";

const allKeys: readonly EndpointKey[] = ["speaker", "playPause", "next", "previous", "power"];

function allSpecs(): EndpointSpec[] {
  const specs = endpointSpecs(names, seed);
  return allKeys.map((key) => specs[key]);
}

describe("endpointSpecs — §2.2 device table", () => {
  it("builds all five endpoints of the table", () => {
    expect(Object.keys(endpointSpecs(names, seed)).sort()).toEqual([...allKeys].sort());
  });

  it("models the speaker as the one Speaker device and the rest as plugs", () => {
    const specs = endpointSpecs(names, seed);
    expect(specs.speaker.kind).toBe("speaker");
    expect(specs.playPause.kind).toBe("onOffPlug");
    expect(specs.next.kind).toBe("onOffPlug");
    expect(specs.previous.kind).toBe("onOffPlug");
    expect(specs.power.kind).toBe("onOffPlug");
  });

  it("keys each spec by its own role", () => {
    const specs = endpointSpecs(names, seed);
    for (const key of allKeys) {
      expect(specs[key].key).toBe(key);
    }
  });

  it("uses the configured display name as the endpoint name (voice target)", () => {
    const specs = endpointSpecs(names, seed);
    for (const key of allKeys) {
      expect(specs[key].info.name).toBe(names[key]);
    }
  });

  it("classifies exactly playPause/next/previous as momentary", () => {
    expect(MOMENTARY_ENDPOINT_KEYS).toEqual(["playPause", "next", "previous"]);
  });
});

describe("endpointSpecs — stable identity", () => {
  it("derives fixed lowercase endpoint ids from the role, not the name", () => {
    const specs = endpointSpecs(names, seed);
    expect(specs.speaker.info.id).toBe("speaker");
    expect(specs.playPause.info.id).toBe("playpause");
    expect(specs.next.info.id).toBe("next");
    expect(specs.previous.info.id).toBe("previous");
    expect(specs.power.info.id).toBe("power");
  });

  it("is deterministic: the same seed always yields the same identity", () => {
    expect(endpointSpecs(names, seed)).toEqual(endpointSpecs(names, seed));
  });

  it("keeps identity unchanged when a device is renamed", () => {
    const renamed = endpointSpecs({ ...names, playPause: "Media Toggle" }, seed);
    const original = endpointSpecs(names, seed);
    expect(renamed.playPause.info.id).toBe(original.playPause.info.id);
    expect(renamed.playPause.info.serialNumber).toBe(original.playPause.info.serialNumber);
    expect(renamed.playPause.info.uniqueId).toBe(original.playPause.info.uniqueId);
  });

  it("changes every serialNumber and uniqueId when the seed changes", () => {
    const other = endpointSpecs(names, "other-seed");
    const original = endpointSpecs(names, seed);
    for (const key of allKeys) {
      expect(other[key].info.serialNumber).not.toBe(original[key].info.serialNumber);
      expect(other[key].info.uniqueId).not.toBe(original[key].info.uniqueId);
    }
  });

  it("gives every endpoint a distinct serialNumber and a distinct uniqueId", () => {
    const serials = allSpecs().map((spec) => spec.info.serialNumber);
    const uniqueIds = allSpecs().map((spec) => spec.info.uniqueId);
    expect(new Set(serials).size).toBe(allKeys.length);
    expect(new Set(uniqueIds).size).toBe(allKeys.length);
  });

  it("never reuses a serialNumber as a uniqueId (matter.js warns on equality)", () => {
    for (const spec of allSpecs()) {
      expect(spec.info.uniqueId).not.toBe(spec.info.serialNumber);
    }
  });

  it("fits Matter's 32-character limits for serialNumber and uniqueId", () => {
    for (const spec of allSpecs()) {
      expect(spec.info.serialNumber.length).toBeLessThanOrEqual(32);
      expect(spec.info.uniqueId.length).toBeLessThanOrEqual(32);
    }
  });
});

describe("bridgeIdentity — root node identity", () => {
  it("is deterministic for the same seed", () => {
    expect(bridgeIdentity(seed)).toEqual(bridgeIdentity(seed));
  });

  it("differs from every bridged endpoint's identity", () => {
    const bridge = bridgeIdentity(seed);
    for (const spec of allSpecs()) {
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
