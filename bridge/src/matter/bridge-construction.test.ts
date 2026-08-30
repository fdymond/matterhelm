import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const seam = vi.hoisted(() => ({
  active: false,
  closeCalls: 0,
  createCalls: 0,
  failNextAdd: true,
  plugCallbacks: new Map<string, (on: boolean) => void>(),
  setOnOff: (id: string, on: boolean): Promise<void> => {
    void id;
    void on;
    return Promise.resolve();
  },
  setOnOffCalls: new Array<{ id: string; on: boolean }>(),
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
        addPlug: (info: { id: string }, onCommand: (on: boolean) => void) => {
          if (seam.failNextAdd) {
            seam.failNextAdd = false;
            return Promise.reject(new Error("injected endpoint add failure"));
          }
          seam.plugCallbacks.set(info.id, onCommand);
          return Promise.resolve({
            invokeOnOff: (on: boolean) => {
              onCommand(on);
              return Promise.resolve();
            },
            setOnOff: (on: boolean) => {
              seam.setOnOffCalls.push({ id: info.id, on });
              return seam.setOnOff(info.id, on);
            },
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
  power: { name: "Power", enabled: false, momentary: false },
  custom: [],
};

describe("createBridge construction cleanup", () => {
  beforeEach(() => {
    seam.active = false;
    seam.closeCalls = 0;
    seam.createCalls = 0;
    seam.failNextAdd = true;
    seam.plugCallbacks.clear();
    seam.setOnOff = () => Promise.resolve();
    seam.setOnOffCalls.length = 0;
  });

  afterEach(() => {
    vi.useRealTimers();
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

  it("retains every built-in and default custom state while resetting only an opted-in custom", async () => {
    vi.useFakeTimers();
    seam.failNextAdd = false;
    const writes: unknown[] = [];
    const handle = await createBridge({
      storageDir: "test-storage",
      momentaryResetMs: 0,
      endpoints: {
        speaker: { name: "Speaker", enabled: false },
        playPause: { name: "Play Pause", enabled: true },
        next: { name: "Next", enabled: true },
        previous: { name: "Previous", enabled: true },
        power: { name: "Power", enabled: true, momentary: false },
        custom: [
          { key: "stateful", name: "Stateful", resetAfterActivation: false },
          { key: "momentary", name: "Momentary", resetAfterActivation: true },
        ],
      },
      onClusterWrite: (write) => writes.push(write),
    });

    await handle.invokePlugOnOff("playpause", true);
    await handle.invokePlugOnOff("next", true);
    await handle.invokePlugOnOff("previous", true);
    await handle.invokePlugOnOff("custom-stateful", true);
    await handle.invokePlugOnOff("custom-momentary", true);
    vi.advanceTimersByTime(0);
    await Promise.resolve();

    expect(seam.setOnOffCalls).toEqual([{ id: "custom-momentary", on: false }]);
    expect(writes).toHaveLength(5);
    expect(writes.at(-1)).toEqual({
      endpoint: "custom",
      key: "momentary",
      cluster: "onOff",
      on: true,
      resetAfterActivation: true,
    });

    await handle.close();
  });

  it("writes irreversible Power back to On promptly without waiting for the custom reset", async () => {
    vi.useFakeTimers();
    seam.failNextAdd = false;
    const writes: unknown[] = [];
    const handle = await createBridge({
      storageDir: "test-storage",
      momentaryResetMs: 2000,
      endpoints: {
        speaker: { name: "Speaker", enabled: false },
        playPause: { name: "Play Pause", enabled: false },
        next: { name: "Next", enabled: false },
        previous: { name: "Previous", enabled: false },
        power: { name: "Power", enabled: true, momentary: true },
        custom: [{ key: "slow", name: "Slow", resetAfterActivation: true }],
      },
      onClusterWrite: (write) => writes.push(write),
    });

    await handle.invokePlugOnOff("power", false);
    await handle.invokePlugOnOff("custom-slow", true);
    expect(seam.setOnOffCalls).toEqual([]);

    vi.advanceTimersByTime(0);
    await Promise.resolve();
    expect(seam.setOnOffCalls).toEqual([{ id: "power", on: true }]);
    expect(writes[0]).toEqual({
      endpoint: "power",
      cluster: "onOff",
      on: false,
      momentary: true,
    });
    expect(writes).toHaveLength(2);

    vi.advanceTimersByTime(2000);
    await Promise.resolve();
    expect(seam.setOnOffCalls).toEqual([
      { id: "power", on: true },
      { id: "custom-slow", on: false },
    ]);
    // Both local writes bypass the command observer and add no dispatch.
    expect(writes).toHaveLength(2);

    await handle.close();
  });

  it("catches and context-logs a rejected reset without emitting an unhandled rejection", async () => {
    vi.useFakeTimers();
    seam.failNextAdd = false;
    seam.setOnOff = () => Promise.reject(new Error("injected reset failure"));
    const error = vi.fn();
    const unhandled: unknown[] = [];
    const onUnhandled = (reason: unknown): void => {
      unhandled.push(reason);
    };
    process.on("unhandledRejection", onUnhandled);

    try {
      const handle = await createBridge({
        storageDir: "test-storage",
        momentaryResetMs: 0,
        endpoints: {
          speaker: { name: "Speaker", enabled: false },
          playPause: { name: "Play Pause", enabled: false },
          next: { name: "Next", enabled: false },
          previous: { name: "Previous", enabled: false },
          power: { name: "Power", enabled: false, momentary: false },
          custom: [{ key: "momentary", name: "Momentary", resetAfterActivation: true }],
        },
        logger: {
          debug: vi.fn(),
          info: vi.fn(),
          warn: vi.fn(),
          error,
          fatal: vi.fn(),
        },
        onClusterWrite: () => undefined,
      });

      await handle.invokePlugOnOff("custom-momentary", true);
      await vi.advanceTimersByTimeAsync(0);
      await Promise.resolve();

      expect(error).toHaveBeenCalledOnce();
      expect(error).toHaveBeenCalledWith(
        {
          evt: "matter.reset.error",
          endpointId: "custom-momentary",
          policy: "custom.resetAfterActivation",
          targetOnOff: false,
          err: "Error: injected reset failure",
        },
        "failed to reset Matter endpoint after activation",
      );
      expect(unhandled).toEqual([]);
      await handle.close();
    } finally {
      process.off("unhandledRejection", onUnhandled);
    }
  });

  it("waits for an already-fired reset before closing the Matter node", async () => {
    vi.useFakeTimers();
    seam.failNextAdd = false;
    let releaseReset = (): void => undefined;
    seam.setOnOff = () =>
      new Promise<void>((resolve) => {
        releaseReset = resolve;
      });
    const handle = await createBridge({
      storageDir: "test-storage",
      momentaryResetMs: 0,
      endpoints: {
        speaker: { name: "Speaker", enabled: false },
        playPause: { name: "Play Pause", enabled: false },
        next: { name: "Next", enabled: false },
        previous: { name: "Previous", enabled: false },
        power: { name: "Power", enabled: false, momentary: false },
        custom: [{ key: "momentary", name: "Momentary", resetAfterActivation: true }],
      },
      onClusterWrite: () => undefined,
    });

    await handle.invokePlugOnOff("custom-momentary", true);
    await vi.advanceTimersByTimeAsync(0);
    const close = handle.close();
    await Promise.resolve();

    expect(seam.setOnOffCalls).toEqual([{ id: "custom-momentary", on: false }]);
    expect(seam.closeCalls).toBe(0);
    releaseReset();
    await close;
    expect(seam.closeCalls).toBe(1);
  });
});
