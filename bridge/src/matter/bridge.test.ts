/**
 * Specification tests for matter/bridge.ts's controller-free units: echo
 * suppression, momentary reset scheduling (fake timers, per
 * docs/ENGINEERING-STANDARDS.md), and endpoint-event -> ClusterWrite
 * translation. Paths needing a live matter.js node are covered by the boot
 * smoke script (src/matter/smoke.ts), not by brittle mocks.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
  EchoSuppressor,
  MOMENTARY_RESET_MS,
  MomentaryResetScheduler,
  endpointEventToClusterWrite,
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
      MOMENTARY_RESET_MS,
      (endpoint) => {
        resets.push(endpoint);
      },
    );
    return { scheduler, resets };
  }

  it("resets a momentary endpoint exactly 800 ms after its on write", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("playPause");
    vi.advanceTimersByTime(MOMENTARY_RESET_MS - 1);
    expect(resets).toEqual([]);
    vi.advanceTimersByTime(1);
    expect(resets).toEqual(["playPause"]);
  });

  it("fires only once per on write", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("next");
    vi.advanceTimersByTime(MOMENTARY_RESET_MS * 5);
    expect(resets).toEqual(["next"]);
  });

  it("restarts the window when a second on write lands before the reset", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("playPause");
    vi.advanceTimersByTime(MOMENTARY_RESET_MS - 100);
    scheduler.noteOn("playPause"); // rapid double-tap: last tap wins
    vi.advanceTimersByTime(MOMENTARY_RESET_MS - 1);
    expect(resets).toEqual([]);
    vi.advanceTimersByTime(1);
    expect(resets).toEqual(["playPause"]);
  });

  it("cancels the pending reset when the endpoint is written off", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("previous");
    scheduler.noteOff("previous");
    vi.advanceTimersByTime(MOMENTARY_RESET_MS * 2);
    expect(resets).toEqual([]);
  });

  it("tolerates an off write with no pending reset", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOff("previous");
    vi.advanceTimersByTime(MOMENTARY_RESET_MS * 2);
    expect(resets).toEqual([]);
  });

  it("times each endpoint independently", () => {
    const { scheduler, resets } = makeScheduler();
    scheduler.noteOn("playPause");
    vi.advanceTimersByTime(300);
    scheduler.noteOn("next");
    vi.advanceTimersByTime(MOMENTARY_RESET_MS - 300);
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
    vi.advanceTimersByTime(MOMENTARY_RESET_MS * 2);
    expect(resets).toEqual([]);
  });

  it("uses the §2.2 reset delay of 800 ms", () => {
    expect(MOMENTARY_RESET_MS).toBe(800);
  });

  it("schedules custom-plug endpoint ids exactly like built-in keys (ADR-004)", () => {
    const resets: string[] = [];
    const scheduler = new MomentaryResetScheduler(MOMENTARY_RESET_MS, (endpoint: string) => {
      resets.push(endpoint);
    });
    scheduler.noteOn("custom-movie-mode");
    scheduler.noteOn("playpause");
    scheduler.noteOff("playpause");
    vi.advanceTimersByTime(MOMENTARY_RESET_MS);
    expect(resets).toEqual(["custom-movie-mode"]);
    scheduler.clear();
  });
});

describe("endpointEventToClusterWrite — synthetic endpoint events", () => {
  it("maps a speaker OnOff change to a speaker onOff write", () => {
    expect(endpointEventToClusterWrite({ key: "speaker", attribute: "onOff", on: true })).toEqual({
      endpoint: "speaker",
      cluster: "onOff",
      on: true,
    });
  });

  it("maps a speaker level change to a levelControl write", () => {
    expect(endpointEventToClusterWrite({ key: "speaker", attribute: "level", level: 127 })).toEqual(
      { endpoint: "speaker", cluster: "levelControl", level: 127 },
    );
  });

  it("maps a null level (matter.js 'no level set') to no write", () => {
    expect(endpointEventToClusterWrite({ key: "speaker", attribute: "level", level: null })).toBe(
      null,
    );
  });

  it.each(["playPause", "next", "previous"] as const)(
    "maps a %s on write to its momentary onOff write",
    (key) => {
      expect(endpointEventToClusterWrite({ key, attribute: "onOff", on: true })).toEqual({
        endpoint: key,
        cluster: "onOff",
        on: true,
      });
    },
  );

  it("passes a momentary off write through (mapping/actions.ts drops it)", () => {
    expect(
      endpointEventToClusterWrite({ key: "playPause", attribute: "onOff", on: false }),
    ).toEqual({ endpoint: "playPause", cluster: "onOff", on: false });
  });

  it("maps power OnOff changes to the stateful power endpoint's writes", () => {
    expect(endpointEventToClusterWrite({ key: "power", attribute: "onOff", on: true })).toEqual({
      endpoint: "power",
      cluster: "onOff",
      on: true,
    });
    expect(endpointEventToClusterWrite({ key: "power", attribute: "onOff", on: false })).toEqual({
      endpoint: "power",
      cluster: "onOff",
      on: false,
    });
  });

  it("maps a custom plug's on write to a custom write carrying its key (ADR-004)", () => {
    expect(
      endpointEventToClusterWrite({
        key: "custom",
        customKey: "movie-mode",
        attribute: "onOff",
        on: true,
      }),
    ).toEqual({ endpoint: "custom", key: "movie-mode", cluster: "onOff", on: true });
  });

  it("passes a custom plug's off write through (mapping/actions.ts drops it)", () => {
    expect(
      endpointEventToClusterWrite({
        key: "custom",
        customKey: "movie-mode",
        attribute: "onOff",
        on: false,
      }),
    ).toEqual({ endpoint: "custom", key: "movie-mode", cluster: "onOff", on: false });
  });
});
