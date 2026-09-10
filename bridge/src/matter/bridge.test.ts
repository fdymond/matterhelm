/**
 * Specification tests for matter/bridge.ts's controller-free units: echo
 * suppression, momentary reset scheduling (fake timers, per
 * docs/ENGINEERING-STANDARDS.md), plug command handling (ADR-008), and
 * endpoint-event -> ClusterWrite translation. Paths needing a live matter.js
 * node are covered by the boot smoke script (src/matter/smoke.ts), not by
 * brittle mocks.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ClusterWrite } from "../mapping/actions.js";

import {
  DEFAULT_MOMENTARY_RESET_MS,
  ECHO_EXPECTATION_CAP,
  ECHO_EXPECTATION_TTL_MS,
  POWER_MOMENTARY_RESET_MS,
  EchoSuppressor,
  MomentaryResetScheduler,
  SerializedSpeakerStateWriter,
  endpointEventToClusterWrite,
  makePlugCommandHandler,
  makePowerCommandHandler,
  type EndpointEvent,
} from "./bridge.js";

describe("EchoSuppressor — value-based FIFO suppression of local writes", () => {
  it("does not suppress an event with no outstanding local write", () => {
    const suppressor = new EchoSuppressor();
    expect(suppressor.check("speaker.onOff", true)).toBe(false);
  });

  it("suppresses the change event of an expected local write exactly once", () => {
    const suppressor = new EchoSuppressor();
    suppressor.expect("speaker.level", 127);
    expect(suppressor.check("speaker.level", 127)).toBe(true);
    expect(suppressor.check("speaker.level", 127)).toBe(false);
  });

  it("consumes expectations in FIFO order across queued local writes", () => {
    const suppressor = new EchoSuppressor();
    suppressor.expect("speaker.level", 100);
    suppressor.expect("speaker.level", 200);
    expect(suppressor.check("speaker.level", 100)).toBe(true);
    expect(suppressor.check("speaker.level", 200)).toBe(true);
    expect(suppressor.check("speaker.level", 200)).toBe(false);
  });

  it("lets a non-matching (remote) value through and keeps the expectation", () => {
    const suppressor = new EchoSuppressor();
    suppressor.expect("speaker.onOff", true);
    // Remote write of `false` interleaves before our own event arrives.
    expect(suppressor.check("speaker.onOff", false)).toBe(false);
    // Our own write's event still gets suppressed afterwards.
    expect(suppressor.check("speaker.onOff", true)).toBe(true);
  });

  it("tracks each attribute key independently", () => {
    const suppressor = new EchoSuppressor();
    suppressor.expect("speaker.onOff", true);
    expect(suppressor.check("speaker.level", 254)).toBe(false);
    expect(suppressor.check("speaker.onOff", true)).toBe(true);
  });

  it("evicts an unmatched expectation after its TTL and logs at debug", () => {
    vi.useFakeTimers();
    vi.setSystemTime(0);
    const logger = {
      debug: vi.fn(),
      info: vi.fn(),
      warn: vi.fn(),
      error: vi.fn(),
      fatal: vi.fn(),
    };
    const suppressor = new EchoSuppressor(logger);
    suppressor.expect("speaker.level", 127);

    vi.advanceTimersByTime(ECHO_EXPECTATION_TTL_MS);

    expect(suppressor.check("speaker.level", 127)).toBe(false);
    expect(logger.debug).toHaveBeenCalledWith(
      { evt: "matter.echo-expectation.evicted", key: "speaker.level", reason: "ttl" },
      "evicted unmatched speaker echo expectation",
    );
    vi.useRealTimers();
  });

  it("caps unmatched expectations and evicts the oldest at debug", () => {
    let now = 0;
    const logger = {
      debug: vi.fn(),
      info: vi.fn(),
      warn: vi.fn(),
      error: vi.fn(),
      fatal: vi.fn(),
    };
    const suppressor = new EchoSuppressor(logger, () => now);
    for (let value = 0; value < ECHO_EXPECTATION_CAP; value += 1) {
      suppressor.expect("speaker.level", value);
      now += 1;
    }

    suppressor.expect("speaker.level", ECHO_EXPECTATION_CAP);

    expect(suppressor.check("speaker.level", 0)).toBe(false);
    expect(suppressor.check("speaker.level", 1)).toBe(true);
    expect(logger.debug).toHaveBeenCalledWith(
      { evt: "matter.echo-expectation.evicted", key: "speaker.level", reason: "cap" },
      "evicted unmatched speaker echo expectation",
    );
  });

  it("serializes racing same-value speaker writes and enqueues one expectation", async () => {
    const suppressor = new EchoSuppressor();
    let level = 0;
    let releaseWrite = (): void => undefined;
    const writeGate = new Promise<void>((resolve) => {
      releaseWrite = resolve;
    });
    const writes: { level?: number; onOff?: boolean }[] = [];
    const writer = new SerializedSpeakerStateWriter(
      {
        getLevel: () => level,
        getOnOff: () => true,
        setState: async (patch) => {
          writes.push(patch);
          await writeGate;
          level = patch.level ?? level;
        },
      },
      suppressor,
    );

    const first = writer.setState(100, true);
    const second = writer.setState(100, true);
    await Promise.resolve();
    expect(writes).toEqual([{ level: 100 }]);
    releaseWrite();
    await Promise.all([first, second]);

    expect(writes).toEqual([{ level: 100 }]);
    expect(suppressor.check("speaker.level", 100)).toBe(true);
    expect(suppressor.check("speaker.level", 100)).toBe(false);
  });

  it("coalesces queued speaker updates into one latest-state slot", async () => {
    const suppressor = new EchoSuppressor();
    let level = 0;
    let onOff = true;
    let releaseWrite = (): void => undefined;
    const writeGate = new Promise<void>((resolve) => {
      releaseWrite = resolve;
    });
    const writes: { level?: number; onOff?: boolean }[] = [];
    const writer = new SerializedSpeakerStateWriter(
      {
        getLevel: () => level,
        getOnOff: () => onOff,
        setState: async (patch) => {
          writes.push(patch);
          if (writes.length === 1) {
            await writeGate;
          }
          level = patch.level ?? level;
          onOff = patch.onOff ?? onOff;
        },
      },
      suppressor,
    );

    const active = writer.setState(50, true);
    const replaced = writer.setState(100, false);
    const latest = writer.setState(200, true);
    expect(replaced).toBe(latest);
    expect(writes).toEqual([{ level: 50 }]);

    releaseWrite();
    await Promise.all([active, replaced, latest]);

    expect(writes).toEqual([{ level: 50 }, { level: 200 }]);
  });

  it("rolls back rejected speaker expectations so a later genuine change dispatches", async () => {
    const suppressor = new EchoSuppressor();
    const writer = new SerializedSpeakerStateWriter(
      {
        getLevel: () => 0,
        getOnOff: () => true,
        setState: () => Promise.reject(new Error("injected speaker write failure")),
      },
      suppressor,
    );
    const dispatched: ClusterWrite[] = [];

    await expect(writer.setState(100, true)).rejects.toThrow("injected speaker write failure");
    if (!suppressor.check("speaker.level", 100)) {
      const write = endpointEventToClusterWrite({ kind: "speakerLevel", level: 100 });
      if (write !== null) dispatched.push(write);
    }

    expect(dispatched).toEqual([{ endpoint: "speaker", cluster: "levelControl", level: 100 }]);
  });
});

/**
 * Fixed non-zero delay for the window-behavior specs below — deliberately NOT
 * {@link DEFAULT_MOMENTARY_RESET_MS}, which is 0 since S8-2 (immediate reset)
 * and would collapse every "before the window elapses" assertion.
 */
const TEST_RESET_MS = 300;

describe("MomentaryResetScheduler — §2.2 auto-reset window", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function makeScheduler(): {
    scheduler: MomentaryResetScheduler;
    resets: string[];
  } {
    const resets: string[] = [];
    const scheduler = new MomentaryResetScheduler(TEST_RESET_MS, (endpoint) => {
      resets.push(endpoint);
    });
    return { scheduler, resets };
  }

  it("resets a momentary endpoint exactly one reset window after its on write", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("custom-movie-mode");
    vi.advanceTimersByTime(TEST_RESET_MS - 1);
    expect(resets).toEqual([]);
    vi.advanceTimersByTime(1);
    expect(resets).toEqual(["custom-movie-mode"]);
  });

  it("fires only once per on write", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("custom-next");
    vi.advanceTimersByTime(TEST_RESET_MS * 5);
    expect(resets).toEqual(["custom-next"]);
  });

  it("restarts the window when a second on write lands before the reset", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("custom-movie-mode");
    vi.advanceTimersByTime(TEST_RESET_MS - 100);
    scheduler.noteOn("custom-movie-mode"); // rapid double-tap: last tap wins
    vi.advanceTimersByTime(TEST_RESET_MS - 1);
    expect(resets).toEqual([]);
    vi.advanceTimersByTime(1);
    expect(resets).toEqual(["custom-movie-mode"]);
  });

  it("cancels the pending reset when the endpoint is written off", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("custom-previous");
    scheduler.noteOff("custom-previous");
    vi.advanceTimersByTime(TEST_RESET_MS * 2);
    expect(resets).toEqual([]);
  });

  it("tolerates an off write with no pending reset", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOff("custom-previous");
    vi.advanceTimersByTime(TEST_RESET_MS * 2);
    expect(resets).toEqual([]);
  });

  it("times each endpoint independently", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("custom-a");
    vi.advanceTimersByTime(300);
    scheduler.noteOn("custom-b");
    vi.advanceTimersByTime(TEST_RESET_MS - 300);
    expect(resets).toEqual(["custom-a"]);
    vi.advanceTimersByTime(300);
    expect(resets).toEqual(["custom-a", "custom-b"]);
  });

  it("clear() cancels every pending reset (bridge close)", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("custom-a");
    scheduler.noteOn("custom-b");
    scheduler.noteOn("custom-c");
    scheduler.clear();
    vi.advanceTimersByTime(TEST_RESET_MS * 2);
    expect(resets).toEqual([]);
  });

  it("defaults the reset delay to 0 ms (immediate, S8-2; must match the tray app's momentaryResetMs default)", () => {
    expect(DEFAULT_MOMENTARY_RESET_MS).toBe(0);
  });

  it("resets on the next tick when the delay is 0 (S8-2 immediate mode)", () => {
    const resets: string[] = [];
    const scheduler = new MomentaryResetScheduler(0, (endpoint: string) => {
      resets.push(endpoint);
    });
    scheduler.noteOn("custom-next");
    // Never synchronously — the On command's handler runs inside the matter.js
    // transaction, and the reset write must land after it commits.
    expect(resets).toEqual([]);
    vi.advanceTimersByTime(0);
    expect(resets).toEqual(["custom-next"]);
    scheduler.clear();
  });

  it("honors a configured (non-default) delay", () => {
    const resets: string[] = [];
    const scheduler = new MomentaryResetScheduler(1234, (endpoint: string) => {
      resets.push(endpoint);
    });
    scheduler.noteOn("playpause");
    vi.advanceTimersByTime(1233);
    expect(resets).toEqual([]);
    vi.advanceTimersByTime(1);
    expect(resets).toEqual(["playpause"]);
    scheduler.clear();
  });

  it("schedules only explicit custom-plug endpoint ids", () => {
    const resets: string[] = [];
    const scheduler = new MomentaryResetScheduler(TEST_RESET_MS, (endpoint: string) => {
      resets.push(endpoint);
    });
    scheduler.noteOn("custom-movie-mode");
    scheduler.noteOn("custom-cancelled");
    scheduler.noteOff("custom-cancelled");
    vi.advanceTimersByTime(TEST_RESET_MS);
    expect(resets).toEqual(["custom-movie-mode"]);
    scheduler.clear();
  });
});

describe("endpointEventToClusterWrite — synthetic endpoint events", () => {
  it("maps a speaker OnOff change to a speaker onOff write", () => {
    expect(endpointEventToClusterWrite({ kind: "speakerOnOff", on: true })).toEqual({
      endpoint: "speaker",
      cluster: "onOff",
      on: true,
    });
  });

  it("maps a speaker level change to a levelControl write", () => {
    expect(endpointEventToClusterWrite({ kind: "speakerLevel", level: 127 })).toEqual({
      endpoint: "speaker",
      cluster: "levelControl",
      level: 127,
    });
  });

  it("maps a null level (matter.js 'no level set') to no write", () => {
    expect(endpointEventToClusterWrite({ kind: "speakerLevel", level: null })).toBe(null);
  });

  it.each(["playPause", "next", "previous"] as const)(
    "maps a %s On command to its momentary onOff write",
    (key) => {
      expect(endpointEventToClusterWrite({ kind: "plugCommand", key, on: true })).toEqual({
        endpoint: key,
        cluster: "onOff",
        on: true,
      });
    },
  );

  it("passes a momentary Off command through (mapping/actions.ts dispatches it as a press, S8-4)", () => {
    expect(
      endpointEventToClusterWrite({ kind: "plugCommand", key: "playPause", on: false }),
    ).toEqual({ endpoint: "playPause", cluster: "onOff", on: false });
  });

  it("maps Power commands with their reversible/momentary policy", () => {
    expect(
      endpointEventToClusterWrite({ kind: "powerCommand", on: true, momentary: false }),
    ).toEqual({
      endpoint: "power",
      cluster: "onOff",
      on: true,
      momentary: false,
    });
    expect(
      endpointEventToClusterWrite({ kind: "powerCommand", on: false, momentary: true }),
    ).toEqual({
      endpoint: "power",
      cluster: "onOff",
      on: false,
      momentary: true,
    });
  });

  it("maps a custom plug's On command to a custom write carrying its key (ADR-004)", () => {
    expect(
      endpointEventToClusterWrite({
        kind: "customCommand",
        customKey: "movie-mode",
        on: true,
        resetAfterActivation: false,
      }),
    ).toEqual({
      endpoint: "custom",
      key: "movie-mode",
      cluster: "onOff",
      on: true,
      resetAfterActivation: false,
    });
  });

  it("preserves reset mode on a custom plug's Off command", () => {
    expect(
      endpointEventToClusterWrite({
        kind: "customCommand",
        customKey: "movie-mode",
        on: false,
        resetAfterActivation: true,
      }),
    ).toEqual({
      endpoint: "custom",
      key: "movie-mode",
      cluster: "onOff",
      on: false,
      resetAfterActivation: true,
    });
  });
});

describe("makePlugCommandHandler — ADR-008 command-driven dispatch", () => {
  function makeRetained(key: "playPause" | "next" | "previous" = "next"): {
    handle: (on: boolean) => void;
    writes: (ClusterWrite | null)[];
    window: string[];
  } {
    const writes: (ClusterWrite | null)[] = [];
    const window: string[] = [];
    const handle = makePlugCommandHandler(
      (on): EndpointEvent => ({ kind: "plugCommand", key, on }),
      (event) => {
        writes.push(endpointEventToClusterWrite(event));
      },
    );
    return { handle, writes, window };
  }

  it("dispatches EVERY repeated On command — the defect ADR-008 fixes", () => {
    const { handle, writes } = makeRetained();
    handle(true);
    handle(true);
    handle(true);
    // Pre-ADR-008 the 2nd and 3rd were invisible: the attribute was already
    // `true`, so matter.js emitted no change event and no action was sent.
    expect(writes).toEqual([
      { endpoint: "next", cluster: "onOff", on: true },
      { endpoint: "next", cluster: "onOff", on: true },
      { endpoint: "next", cluster: "onOff", on: true },
    ]);
  });

  it("retains built-in state and dispatches both transition directions without reset scheduling", () => {
    const { handle, writes, window } = makeRetained("playPause");
    handle(true);
    handle(false);
    expect(window).toEqual([]);
    expect(writes).toEqual([
      { endpoint: "playPause", cluster: "onOff", on: true },
      { endpoint: "playPause", cluster: "onOff", on: false },
    ]);
  });

  it("schedules reset-enabled custom On commands while preserving reset metadata", () => {
    const writes: (ClusterWrite | null)[] = [];
    const window: string[] = [];
    const handle = makePlugCommandHandler(
      (on): EndpointEvent => ({
        kind: "customCommand",
        customKey: "movie-mode",
        on,
        resetAfterActivation: true,
      }),
      (event) => {
        writes.push(endpointEventToClusterWrite(event));
      },
      { noteOn: () => window.push("noteOn"), noteOff: () => window.push("noteOff") },
    );
    handle(true);
    handle(true);
    expect(writes).toEqual([
      {
        endpoint: "custom",
        key: "movie-mode",
        cluster: "onOff",
        on: true,
        resetAfterActivation: true,
      },
      {
        endpoint: "custom",
        key: "movie-mode",
        cluster: "onOff",
        on: true,
        resetAfterActivation: true,
      },
    ]);
    expect(window).toEqual(["noteOn", "noteOn"]);
  });
});

describe("makePowerCommandHandler — split retained/momentary Power policy", () => {
  it("dispatches both reversible edges and never schedules a reset", () => {
    const writes: (ClusterWrite | null)[] = [];
    const window: string[] = [];
    const handle = makePowerCommandHandler(
      false,
      (event) => {
        writes.push(endpointEventToClusterWrite(event));
      },
      { noteOn: () => window.push("noteOn"), noteOff: () => window.push("noteOff") },
    );
    handle(true);
    handle(false);
    expect(writes).toEqual([
      { endpoint: "power", cluster: "onOff", on: true, momentary: false },
      { endpoint: "power", cluster: "onOff", on: false, momentary: false },
    ]);
    expect(window).toEqual([]);
  });

  it("dispatches irreversible Off before scheduling reset-to-On", () => {
    const order: string[] = [];
    const handle = makePowerCommandHandler(
      true,
      (event) => {
        if (event.kind !== "powerCommand") {
          expect.fail("expected a Power command event");
        }
        order.push(`emit:${event.kind}:${String(event.on)}`);
      },
      { noteOn: () => order.push("reset:on"), noteOff: () => order.push("cancel") },
    );

    handle(false);

    expect(order).toEqual(["emit:powerCommand:false", "reset:on"]);
  });

  it("emits irreversible user On for no-action mapping and cancels any pending reset", () => {
    const writes: (ClusterWrite | null)[] = [];
    const window: string[] = [];
    const handle = makePowerCommandHandler(
      true,
      (event) => {
        writes.push(endpointEventToClusterWrite(event));
      },
      { noteOn: () => window.push("noteOn"), noteOff: () => window.push("noteOff") },
    );

    handle(true);

    expect(writes).toEqual([{ endpoint: "power", cluster: "onOff", on: true, momentary: true }]);
    expect(window).toEqual(["noteOff"]);
  });

  it("uses a prompt fixed reset instead of the configured custom-command delay", () => {
    vi.useFakeTimers();
    const writes: (ClusterWrite | null)[] = [];
    const localPowerWrites: boolean[] = [];
    const powerScheduler = new MomentaryResetScheduler(POWER_MOMENTARY_RESET_MS, (endpoint) => {
      expect(endpoint).toBe("power");
      localPowerWrites.push(true);
    });
    const customScheduler = new MomentaryResetScheduler(TEST_RESET_MS, () => undefined);
    const handle = makePowerCommandHandler(
      true,
      (event) => {
        writes.push(endpointEventToClusterWrite(event));
      },
      {
        noteOn: () => {
          powerScheduler.noteOn("power");
        },
        noteOff: () => {
          powerScheduler.noteOff("power");
        },
      },
    );

    handle(false);
    customScheduler.noteOn("custom-slow");
    expect(writes).toEqual([{ endpoint: "power", cluster: "onOff", on: false, momentary: true }]);
    expect(localPowerWrites).toEqual([]);
    vi.advanceTimersByTime(POWER_MOMENTARY_RESET_MS);
    expect(localPowerWrites).toEqual([true]);
    // The local attribute write bypasses the command observer, so no second
    // dispatch descriptor appeared.
    expect(writes).toHaveLength(1);

    powerScheduler.clear();
    customScheduler.clear();
    vi.useRealTimers();
  });
});
