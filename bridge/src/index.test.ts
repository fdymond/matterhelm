import { describe, expect, it, vi } from "vitest";

const seam = vi.hoisted(() => ({
  commissioned: true,
  commissionedCallbacks: new Array<(commissioned: boolean) => void>(),
  frames: new Array<unknown>(),
}));

vi.mock("./config.js", () => ({
  loadConfig: () => ({
    bridgeName: "Test Bridge",
    endpoints: { speaker: { enabled: true } },
    ipcPort: 39531,
    ipcToken: "test-token",
    logLevel: "silent",
    matterLogFacilities: {},
    matterLogLevel: "fatal",
    momentaryResetMs: 0,
    storageDir: "test-storage",
  }),
}));

vi.mock("./ipc/client.js", () => ({
  IpcClient: class {
    send(frame: unknown): void {
      seam.frames.push(frame);
    }

    start(): void {
      return undefined;
    }

    stop(): void {
      return undefined;
    }
  },
}));

vi.mock("./log.js", () => ({
  makeLogger: () => ({
    debug: vi.fn(),
    error: vi.fn(),
    info: vi.fn(),
  }),
}));

vi.mock("./matter/advertisement-health.js", () => ({
  AdvertisementHealthMonitor: class {
    readonly testSeam = true;
  },
  AdvertisementHealthMonitorLifecycle: class {
    close(): void {
      return undefined;
    }

    setCommissioned(): void {
      return undefined;
    }
  },
  probeMatterAdvertisement: vi.fn(),
}));

vi.mock("./matter/bridge.js", () => ({
  createBridge: () =>
    Promise.resolve({
      close: () => Promise.resolve(),
      endpoints: [],
      get isCommissioned(): boolean {
        return seam.commissioned;
      },
      onCommissionedChange: (callback: (commissioned: boolean) => void) => {
        seam.commissionedCallbacks.push(callback);
      },
      get pairingCodes(): { qrPayload: string; manualCode: string } | null {
        return seam.commissioned ? null : { qrPayload: "MT:NEW-CODE", manualCode: "1111-222-3333" };
      },
      setSpeakerState: () => Promise.resolve(),
      start: () => Promise.resolve(),
    }),
}));

vi.mock("./timing.js", () => ({
  PendingAckTimings: class {
    readonly testSeam = true;
  },
  makeAckTimingObserver: () => () => undefined,
  makeActionDispatcher: () => () => undefined,
}));

describe("composition-root commissioning transitions", () => {
  it("emits uncommissioned matterStatus before fresh pairing codes after controller removal", async () => {
    const stdinOn = vi.spyOn(process.stdin, "on").mockImplementation(() => process.stdin);
    const stdinResume = vi.spyOn(process.stdin, "resume").mockImplementation(() => process.stdin);
    await import("./index.js");
    await vi.waitFor(() => {
      expect(seam.commissionedCallbacks).toHaveLength(1);
      expect(seam.frames).toContainEqual({
        v: 3,
        type: "matterStatus",
        commissioned: true,
        advertisement: "notApplicable",
      });
    });
    stdinOn.mockRestore();
    stdinResume.mockRestore();
    seam.frames.length = 0;

    seam.commissioned = false;
    seam.commissionedCallbacks[0]?.(false);

    expect(seam.frames).toEqual([
      {
        v: 3,
        type: "matterStatus",
        commissioned: false,
        advertisement: "checking",
      },
      {
        v: 3,
        type: "pairing",
        qrPayload: "MT:NEW-CODE",
        manualCode: "1111-222-3333",
      },
    ]);
  });
});
