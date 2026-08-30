/**
 * Specification tests for mapping/actions.ts (docs/BLUEPRINT.md §2.2).
 *
 * Test names describe the mapping behaviour, not the function under test.
 */
import { describe, expect, it } from "vitest";

import { PROTOCOL_VERSION } from "../ipc/protocol.js";

import { clusterWriteToAction, levelToVolume, onOffToMuted, type ClusterWrite } from "./actions.js";

const id = "123e4567-e89b-12d3-a456-426614174000";

describe("levelToVolume (0-254 -> 0-100%, round-to-nearest)", () => {
  it("maps the minimum level 0 to 0%", () => {
    expect(levelToVolume(0)).toBe(0);
  });

  it("maps the maximum level 254 to 100%", () => {
    expect(levelToVolume(254)).toBe(100);
  });

  it("maps the exact midpoint 127 to 50%", () => {
    expect(levelToVolume(127)).toBe(50);
  });

  it("rounds level 1 down to 0% (0.39... rounds to 0)", () => {
    expect(levelToVolume(1)).toBe(0);
  });

  it("rounds level 2 up to 1% (0.78... rounds to 1)", () => {
    expect(levelToVolume(2)).toBe(1);
  });

  it("rounds level 253 up to 100% (99.6... rounds to 100)", () => {
    expect(levelToVolume(253)).toBe(100);
  });

  it("clamps a negative level to 0%", () => {
    expect(levelToVolume(-10)).toBe(0);
  });

  it("clamps a level above 254 to 100%", () => {
    expect(levelToVolume(1000)).toBe(100);
  });
});

describe("onOffToMuted (Speaker OnOff polarity: On = unmuted, Off = muted)", () => {
  it("maps On (true) to unmuted (false)", () => {
    expect(onOffToMuted(true)).toBe(false);
  });

  it("maps Off (false) to muted (true)", () => {
    expect(onOffToMuted(false)).toBe(true);
  });
});

describe("clusterWriteToAction — speaker onOff (mute control)", () => {
  it("maps On (true) to setMuted:false", () => {
    const write: ClusterWrite = { endpoint: "speaker", cluster: "onOff", on: true };
    expect(clusterWriteToAction(write, id)).toEqual({
      v: PROTOCOL_VERSION,
      type: "action",
      id,
      name: "setMuted",
      value: false,
    });
  });

  it("maps Off (false) to setMuted:true", () => {
    const write: ClusterWrite = { endpoint: "speaker", cluster: "onOff", on: false };
    expect(clusterWriteToAction(write, id)).toEqual({
      v: PROTOCOL_VERSION,
      type: "action",
      id,
      name: "setMuted",
      value: true,
    });
  });
});

describe("clusterWriteToAction — speaker levelControl (volume)", () => {
  it("maps level 0 to setVolume:0", () => {
    const write: ClusterWrite = { endpoint: "speaker", cluster: "levelControl", level: 0 };
    expect(clusterWriteToAction(write, id)).toEqual({
      v: PROTOCOL_VERSION,
      type: "action",
      id,
      name: "setVolume",
      value: 0,
    });
  });

  it("maps level 254 to setVolume:100", () => {
    const write: ClusterWrite = { endpoint: "speaker", cluster: "levelControl", level: 254 };
    expect(clusterWriteToAction(write, id)).toEqual({
      v: PROTOCOL_VERSION,
      type: "action",
      id,
      name: "setVolume",
      value: 100,
    });
  });

  it("maps level 127 to setVolume:50", () => {
    const write: ClusterWrite = { endpoint: "speaker", cluster: "levelControl", level: 127 };
    expect(clusterWriteToAction(write, id)).toEqual({
      v: PROTOCOL_VERSION,
      type: "action",
      id,
      name: "setVolume",
      value: 50,
    });
  });

  it("maps level 1 to setVolume:0 (rounding edge)", () => {
    const write: ClusterWrite = { endpoint: "speaker", cluster: "levelControl", level: 1 };
    expect(clusterWriteToAction(write, id)).toEqual({
      v: PROTOCOL_VERSION,
      type: "action",
      id,
      name: "setVolume",
      value: 0,
    });
  });
});

describe("clusterWriteToAction — retained-state transport switches", () => {
  it("maps playPause On to dedicated play", () => {
    const write: ClusterWrite = { endpoint: "playPause", cluster: "onOff", on: true };
    expect(clusterWriteToAction(write, id)).toEqual({
      v: PROTOCOL_VERSION,
      type: "action",
      id,
      name: "play",
    });
  });

  it("maps playPause Off to dedicated pause", () => {
    const write: ClusterWrite = { endpoint: "playPause", cluster: "onOff", on: false };
    expect(clusterWriteToAction(write, id)).toEqual({
      v: PROTOCOL_VERSION,
      type: "action",
      id,
      name: "pause",
    });
  });

  it.each(["next", "previous"] as const)("dispatches %s on both transition directions", (name) => {
    for (const on of [true, false]) {
      const write: ClusterWrite = { endpoint: name, cluster: "onOff", on };
      expect(clusterWriteToAction(write, id)).toEqual({
        v: PROTOCOL_VERSION,
        type: "action",
        id,
        name,
      });
    }
  });
});

describe("clusterWriteToAction — custom commands", () => {
  it("dispatches both edges when reset is disabled", () => {
    for (const on of [true, false]) {
      const write: ClusterWrite = {
        endpoint: "custom",
        key: "movie-mode",
        cluster: "onOff",
        on,
        resetAfterActivation: false,
      };
      expect(clusterWriteToAction(write, id)).toEqual({
        v: PROTOCOL_VERSION,
        type: "action",
        id,
        name: "custom",
        key: "movie-mode",
      });
    }
  });

  it("passes the key through verbatim for a different command", () => {
    const write: ClusterWrite = {
      endpoint: "custom",
      key: "stop-media",
      cluster: "onOff",
      on: true,
      resetAfterActivation: false,
    };
    const action = clusterWriteToAction(write, id);
    if (action?.name === "custom") {
      expect(action.key).toBe("stop-media");
    } else {
      expect.fail("expected a custom action frame");
    }
  });

  it("dispatches only On when reset is enabled", () => {
    const onWrite: ClusterWrite = {
      endpoint: "custom",
      key: "movie-mode",
      cluster: "onOff",
      on: true,
      resetAfterActivation: true,
    };
    expect(clusterWriteToAction(onWrite, id)).toEqual({
      v: PROTOCOL_VERSION,
      type: "action",
      id,
      name: "custom",
      key: "movie-mode",
    });
    expect(clusterWriteToAction({ ...onWrite, on: false }, id)).toBeNull();
  });
});

describe("clusterWriteToAction — power endpoint", () => {
  it("maps reversible On to powerOn", () => {
    const write: ClusterWrite = {
      endpoint: "power",
      cluster: "onOff",
      on: true,
      momentary: false,
    };
    expect(clusterWriteToAction(write, id)).toEqual({
      v: PROTOCOL_VERSION,
      type: "action",
      id,
      name: "powerOn",
    });
  });

  it("maps reversible Off to powerOff", () => {
    const write: ClusterWrite = {
      endpoint: "power",
      cluster: "onOff",
      on: false,
      momentary: false,
    };
    expect(clusterWriteToAction(write, id)).toEqual({
      v: PROTOCOL_VERSION,
      type: "action",
      id,
      name: "powerOff",
    });
  });

  it("maps irreversible Off once and ignores user On", () => {
    const offWrite: ClusterWrite = {
      endpoint: "power",
      cluster: "onOff",
      on: false,
      momentary: true,
    };
    expect(clusterWriteToAction(offWrite, id)).toEqual({
      v: PROTOCOL_VERSION,
      type: "action",
      id,
      name: "powerOff",
    });
    expect(clusterWriteToAction({ ...offWrite, on: true }, id)).toBeNull();
  });
});

describe("clusterWriteToAction — id passthrough", () => {
  it("uses the supplied id verbatim rather than generating one", () => {
    const otherId = "00000000-0000-4000-8000-000000000000";
    const write: ClusterWrite = {
      endpoint: "power",
      cluster: "onOff",
      on: true,
      momentary: false,
    };
    const action = clusterWriteToAction(write, otherId);
    expect(action?.id).toBe(otherId);
  });
});
