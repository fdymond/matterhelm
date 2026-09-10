import { beforeAll, describe, expect, it, vi } from "vitest";

import type { handleUnhandledRejection as HandleUnhandledRejection } from "./index.js";

const seam = vi.hoisted(() => ({
  commissioned: true,
  commissionedCallbacks: new Array<(commissioned: boolean) => void>(),
  frameCallbacks: new Array<(frame: unknown) => void>(),
  frames: new Array<unknown>(),
  loggerErrors: new Array<{ obj: Record<string, unknown>; msg: string }>(),
  rejectSpeakerState: false,
  sharedSpeakerStatePromises: new Array<Promise<void>>(),
  speakerStateCalls: new Array<{ level: number; onOff: boolean }>(),
}));

let handleUnhandledRejection: typeof HandleUnhandledRejection;

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
        const shared = seam.sharedSpeakerStatePromises[0];
        if (shared !== undefined) {
          return shared;
        }
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
    ({ handleUnhandledRejection } = await import("./index.js"));
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

  it("logs an unhandled rejection and requests non-zero idempotent shutdown", () => {
    const logger = { error: vi.fn() };
    const shutdown = vi.fn();
    const rejection = new Error("injected unhandled rejection");

    handleUnhandledRejection(rejection, logger, shutdown);

    expect(logger.error).toHaveBeenCalledWith(
      {
        evt: "process.unhandled-rejection",
        err: "Error: injected unhandled rejection",
      },
      "unhandled promise rejection; shutting down for supervisor restart",
    );
    expect(shutdown).toHaveBeenCalledOnce();
    expect(shutdown).toHaveBeenCalledWith("unhandled-rejection", 1);
  });

  it("observes a shared coalesced speaker-state promise only once", async () => {
    seam.loggerErrors.length = 0;
    let rejectShared = (reason: unknown): void => {
      void reason;
    };
    seam.sharedSpeakerStatePromises.push(
      new Promise<void>((_resolve, reject) => {
        rejectShared = reject;
      }),
    );

    seam.frameCallbacks[0]?.({ v: 5, type: "state", volume: 10, muted: false });
    seam.frameCallbacks[0]?.({ v: 5, type: "state", volume: 20, muted: false });
    seam.frameCallbacks[0]?.({ v: 5, type: "state", volume: 30, muted: true });
    rejectShared(new Error("shared queued write failed"));

    await vi.waitFor(() => {
      expect(seam.loggerErrors).toEqual([
        {
          obj: {
            evt: "matter.speaker-state.error",
            err: "Error: shared queued write failed",
          },
          msg: "failed to apply tray state to the speaker endpoint",
        },
      ]);
    });
    seam.sharedSpeakerStatePromises.length = 0;
  });
});
