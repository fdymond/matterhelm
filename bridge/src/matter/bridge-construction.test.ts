import { beforeEach, describe, expect, it, vi } from "vitest";

const seam = vi.hoisted(() => ({
  active: false,
  closeCalls: 0,
  createCalls: 0,
  failNextAdd: true,
}));

vi.mock("./adapter.js", () => ({
  MatterNode: {
    create: () => {
      seam.createCalls += 1;
      if (seam.active) {
        return Promise.reject(new Error("environment still claimed"));
      }
      seam.active = true;
      return Promise.resolve({
        addPlug: () => {
          if (seam.failNextAdd) {
            seam.failNextAdd = false;
            return Promise.reject(new Error("injected endpoint add failure"));
          }
          return Promise.resolve({
            invokeOnOff: () => Promise.resolve(),
            setOnOff: () => Promise.resolve(),
          });
        },
        addSpeaker: () => Promise.reject(new Error("speaker is disabled in this test")),
        close: () => {
          seam.closeCalls += 1;
          seam.active = false;
          return Promise.resolve();
        },
        isCommissioned: false,
        onCommissionedChange: () => undefined,
        pairingCodes: null,
        start: () => Promise.resolve(),
      });
    },
  },
}));

import { createBridge } from "./bridge.js";

const endpoints = {
  speaker: { name: "Speaker", enabled: false },
  playPause: { name: "Play Pause", enabled: true },
  next: { name: "Next", enabled: false },
  previous: { name: "Previous", enabled: false },
  power: { name: "Power", enabled: false },
  custom: [],
};

describe("createBridge construction cleanup", () => {
  beforeEach(() => {
    seam.active = false;
    seam.closeCalls = 0;
    seam.createCalls = 0;
    seam.failNextAdd = true;
  });

  it("closes a partially assembled node so a retry can claim the environment", async () => {
    const options = { storageDir: "test-storage", endpoints, onClusterWrite: () => undefined };

    await expect(createBridge(options)).rejects.toThrow("injected endpoint add failure");
    expect(seam.closeCalls).toBe(1);
    expect(seam.active).toBe(false);

    const retry = await createBridge(options);
    expect(seam.createCalls).toBe(2);
    expect(retry.endpoints).toEqual([{ id: "playpause", name: "Play Pause", kind: "onOffPlug" }]);
    await retry.close();
    expect(seam.closeCalls).toBe(2);
  });
});
