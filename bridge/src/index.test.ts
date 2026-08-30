import { beforeAll, describe, expect, it, vi } from "vitest";

const seam = vi.hoisted(() => ({
  commissioned: true,
  commissionedCallbacks: new Array<(commissioned: boolean) => void>(),
  frameCallbacks: new Array<(frame: unknown) => void>(),
  frames: new Array<unknown>(),
  loggerErrors: new Array<{ obj: Record<string, unknown>; msg: string }>(),
  rejectSpeakerState: false,
  speakerStateCalls: new Array<{ level: number; onOff: boolean }>(),
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
    constructor(options: { onFrame: (frame: unknown) => void }) {
      seam.frameCallbacks.push(options.onFrame);
    }

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
    error: (obj: Record<string, unknown>, msg: string) => {
      seam.loggerErrors.push({ obj, msg });
    },
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
      setSpeakerState: (level: number, onOff: boolean) => {
        seam.speakerStateCalls.push({ level, onOff });
        if (seam.rejectSpeakerState) {
          seam.rejectSpeakerState = false;
          return Promise.reject(new Error("injected speaker state failure"));
        }
        return Promise.resolve();
      },
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
  beforeAll(async () => {
    const stdinOn = vi.spyOn(process.stdin, "on").mockImplementation(() => process.stdin);
    const stdinResume = vi.spyOn(process.stdin, "resume").mockImplementation(() => process.stdin);
    await import("./index.js");
    await vi.waitFor(() => {
      expect(seam.commissionedCallbacks).toHaveLength(1);
      expect(seam.frameCallbacks).toHaveLength(1);
    });
    stdinOn.mockRestore();
    stdinResume.mockRestore();
  });

  it("emits uncommissioned matterStatus before fresh pairing codes after controller removal", async () => {
    await vi.waitFor(() => {
      expect(seam.frames).toContainEqual({
        v: 5,
        type: "matterStatus",
        commissioned: true,
        advertisement: "notApplicable",
      });
    });
    seam.frames.length = 0;

    seam.commissioned = false;
    seam.commissionedCallbacks[0]?.(false);

    expect(seam.frames).toEqual([
      {
        v: 5,
        type: "matterStatus",
        commissioned: false,
        advertisement: "checking",
      },
      {
        v: 5,
        type: "pairing",
        qrPayload: "MT:NEW-CODE",
        manualCode: "1111-222-3333",
      },
    ]);
  });

  it("logs and survives a rejected setSpeakerState call", async () => {
    const exit = vi.spyOn(process, "exit").mockImplementation((code): never => {
      throw new Error(`unexpected process.exit(${String(code)})`);
    });
    seam.loggerErrors.length = 0;
    seam.speakerStateCalls.length = 0;
    seam.rejectSpeakerState = true;

    seam.frameCallbacks[0]?.({ v: 5, type: "state", volume: 40, muted: false });
    await vi.waitFor(() => {
      expect(seam.loggerErrors).toEqual([
        {
          obj: {
            evt: "matter.speaker-state.error",
            err: "Error: injected speaker state failure",
          },
          msg: "failed to apply tray state to the speaker endpoint",
        },
      ]);
    });

    seam.frameCallbacks[0]?.({ v: 5, type: "state", volume: 50, muted: true });
    await vi.waitFor(() => {
      expect(seam.speakerStateCalls).toEqual([
        { level: 102, onOff: true },
        { level: 127, onOff: false },
      ]);
    });
    expect(exit).not.toHaveBeenCalled();
    exit.mockRestore();
  });
});
