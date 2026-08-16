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
  EchoSuppressor,
  MomentaryResetScheduler,
  endpointEventToClusterWrite,
  makePlugCommandHandler,
  type EndpointEvent,
  type MomentaryEndpointKey,
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
});

describe("MomentaryResetScheduler — §2.2 auto-reset window", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function makeScheduler(): {
    scheduler: MomentaryResetScheduler<MomentaryEndpointKey>;
    resets: MomentaryEndpointKey[];
  } {
    const resets: MomentaryEndpointKey[] = [];
    const scheduler = new MomentaryResetScheduler<MomentaryEndpointKey>(
      DEFAULT_MOMENTARY_RESET_MS,
      (endpoint) => {
        resets.push(endpoint);
      },
    );
    return { scheduler, resets };
  }

  it("resets a momentary endpoint exactly one reset window after its on write", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("playPause");
    vi.advanceTimersByTime(DEFAULT_MOMENTARY_RESET_MS - 1);
    expect(resets).toEqual([]);
    vi.advanceTimersByTime(1);
    expect(resets).toEqual(["playPause"]);
  });

  it("fires only once per on write", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("next");
    vi.advanceTimersByTime(DEFAULT_MOMENTARY_RESET_MS * 5);
    expect(resets).toEqual(["next"]);
  });

  it("restarts the window when a second on write lands before the reset", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("playPause");
    vi.advanceTimersByTime(DEFAULT_MOMENTARY_RESET_MS - 100);
    scheduler.noteOn("playPause"); // rapid double-tap: last tap wins
    vi.advanceTimersByTime(DEFAULT_MOMENTARY_RESET_MS - 1);
    expect(resets).toEqual([]);
    vi.advanceTimersByTime(1);
    expect(resets).toEqual(["playPause"]);
  });

  it("cancels the pending reset when the endpoint is written off", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("previous");
    scheduler.noteOff("previous");
    vi.advanceTimersByTime(DEFAULT_MOMENTARY_RESET_MS * 2);
    expect(resets).toEqual([]);
  });

  it("tolerates an off write with no pending reset", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOff("previous");
    vi.advanceTimersByTime(DEFAULT_MOMENTARY_RESET_MS * 2);
    expect(resets).toEqual([]);
  });

  it("times each endpoint independently", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("playPause");
    vi.advanceTimersByTime(300);
    scheduler.noteOn("next");
    vi.advanceTimersByTime(DEFAULT_MOMENTARY_RESET_MS - 300);
    expect(resets).toEqual(["playPause"]);
    vi.advanceTimersByTime(300);
    expect(resets).toEqual(["playPause", "next"]);
  });

  it("clear() cancels every pending reset (bridge close)", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("playPause");
    scheduler.noteOn("next");
    scheduler.noteOn("previous");
    scheduler.clear();
    vi.advanceTimersByTime(DEFAULT_MOMENTARY_RESET_MS * 2);
    expect(resets).toEqual([]);
  });

  it("defaults the reset delay to 300 ms (S7-1; must match the tray app's momentaryResetMs default)", () => {
    expect(DEFAULT_MOMENTARY_RESET_MS).toBe(300);
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

  it("schedules custom-plug endpoint ids exactly like built-in keys (ADR-004)", () => {
    const resets: string[] = [];
    const scheduler = new MomentaryResetScheduler(
      DEFAULT_MOMENTARY_RESET_MS,
      (endpoint: string) => {
        resets.push(endpoint);
      },
    );
    scheduler.noteOn("custom-movie-mode");
    scheduler.noteOn("playpause");
    scheduler.noteOff("playpause");
    vi.advanceTimersByTime(DEFAULT_MOMENTARY_RESET_MS);
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

  it("passes a momentary Off command through (mapping/actions.ts drops it)", () => {
    expect(
      endpointEventToClusterWrite({ kind: "plugCommand", key: "playPause", on: false }),
    ).toEqual({ endpoint: "playPause", cluster: "onOff", on: false });
  });

  it("maps power OnOff commands to the stateful power endpoint's writes", () => {
    expect(endpointEventToClusterWrite({ kind: "plugCommand", key: "power", on: true })).toEqual({
      endpoint: "power",
      cluster: "onOff",
      on: true,
    });
    expect(endpointEventToClusterWrite({ kind: "plugCommand", key: "power", on: false })).toEqual({
      endpoint: "power",
      cluster: "onOff",
      on: false,
    });
  });

  it("maps a custom plug's On command to a custom write carrying its key (ADR-004)", () => {
    expect(
      endpointEventToClusterWrite({ kind: "customCommand", customKey: "movie-mode", on: true }),
    ).toEqual({ endpoint: "custom", key: "movie-mode", cluster: "onOff", on: true });
  });

  it("passes a custom plug's Off command through (mapping/actions.ts drops it)", () => {
    expect(
      endpointEventToClusterWrite({ kind: "customCommand", customKey: "movie-mode", on: false }),
    ).toEqual({ endpoint: "custom", key: "movie-mode", cluster: "onOff", on: false });
  });
});

describe("makePlugCommandHandler — ADR-008 command-driven dispatch", () => {
  function makeMomentary(key: "playPause" | "next" | "previous" = "next"): {
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
      {
        noteOn: () => window.push("noteOn"),
        noteOff: () => window.push("noteOff"),
      },
    );
    return { handle, writes, window };
  }

  it("dispatches EVERY repeated On command — the defect ADR-008 fixes", () => {
    const { handle, writes } = makeMomentary();
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

  it("re-arms the reset window on every On command (last press wins)", () => {
    const { handle, window } = makeMomentary();
    handle(true);
    handle(true);
    expect(window).toEqual(["noteOn", "noteOn"]);
  });

  it("cancels the pending reset on an Off command and dispatches no action", () => {
    const { handle, writes, window } = makeMomentary("playPause");
    handle(true);
    handle(false);
    expect(window).toEqual(["noteOn", "noteOff"]);
    // The Off write reaches mapping/actions.ts, which drops it (no action).
    expect(writes).toEqual([
      { endpoint: "playPause", cluster: "onOff", on: true },
      { endpoint: "playPause", cluster: "onOff", on: false },
    ]);
  });

  it("dispatches a custom plug's repeated On commands exactly like a built-in", () => {
    const writes: (ClusterWrite | null)[] = [];
    const window: string[] = [];
    const handle = makePlugCommandHandler(
      (on): EndpointEvent => ({ kind: "customCommand", customKey: "movie-mode", on }),
      (event) => {
        writes.push(endpointEventToClusterWrite(event));
      },
      { noteOn: () => window.push("noteOn"), noteOff: () => window.push("noteOff") },
    );
    handle(true);
    handle(true);
    expect(writes).toEqual([
      { endpoint: "custom", key: "movie-mode", cluster: "onOff", on: true },
      { endpoint: "custom", key: "movie-mode", cluster: "onOff", on: true },
    ]);
    expect(window).toEqual(["noteOn", "noteOn"]);
  });

  it("dispatches a repeated Off command on the stateful power plug (no reset window)", () => {
    const writes: (ClusterWrite | null)[] = [];
    const handle = makePlugCommandHandler(
      (on): EndpointEvent => ({ kind: "plugCommand", key: "power", on }),
      (event) => {
        writes.push(endpointEventToClusterWrite(event));
      },
    );
    handle(false);
    handle(false);
    // "Hey Google, turn off HTPC Power" when it already reads off used to do
    // nothing at all; both invocations now reach the executor.
    expect(writes).toEqual([
      { endpoint: "power", cluster: "onOff", on: false },
      { endpoint: "power", cluster: "onOff", on: false },
    ]);
  });
});
