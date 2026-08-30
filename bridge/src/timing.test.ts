import { describe, expect, it } from "vitest";

import type { ActionFrame } from "./ipc/protocol.js";
import {
  PENDING_ACK_MAX_ENTRIES,
  PENDING_ACK_TTL_MS,
  PendingAckTimings,
  makeAckTimingObserver,
  makeActionDispatcher,
  roundToMicros,
} from "./timing.js";
import type { TimingLogger } from "./timing.js";

/** Injectable clock: `tick()` advances, `now` is handed to the map. */
function makeClock(startMs = 0): { now: () => number; tick: (ms: number) => void } {
  let current = startMs;
  return {
    now: () => current,
    tick: (ms) => {
      current += ms;
    },
  };
}

describe("roundToMicros — µs-precision elapsed values", () => {
  it("rounds to three decimal places", () => {
    expect(roundToMicros(1.2345678)).toBe(1.235);
    expect(roundToMicros(0.0004)).toBe(0);
    expect(roundToMicros(0.0005)).toBe(0.001);
  });

  it("leaves integral values untouched", () => {
    expect(roundToMicros(42)).toBe(42);
    expect(roundToMicros(0)).toBe(0);
  });
});

describe("PendingAckTimings — bounded send->ack latency map", () => {
  it("measures elapsed time between note and settle", () => {
    const clock = makeClock();
    const timings = new PendingAckTimings(PENDING_ACK_TTL_MS, PENDING_ACK_MAX_ENTRIES, clock.now);
    timings.note("id-1");
    clock.tick(12.3456789);
    expect(timings.settle("id-1")).toBe(12.346);
  });

  it("returns null for an unknown id", () => {
    const timings = new PendingAckTimings(
      PENDING_ACK_TTL_MS,
      PENDING_ACK_MAX_ENTRIES,
      makeClock().now,
    );
    expect(timings.settle("never-noted")).toBeNull();
  });

  it("consumes the entry: a duplicate ack settles to null", () => {
    const clock = makeClock();
    const timings = new PendingAckTimings(PENDING_ACK_TTL_MS, PENDING_ACK_MAX_ENTRIES, clock.now);
    timings.note("id-1");
    clock.tick(5);
    expect(timings.settle("id-1")).toBe(5);
    expect(timings.settle("id-1")).toBeNull();
  });

  it("tracks concurrent in-flight sends independently", () => {
    const clock = makeClock();
    const timings = new PendingAckTimings(PENDING_ACK_TTL_MS, PENDING_ACK_MAX_ENTRIES, clock.now);
    timings.note("id-1");
    clock.tick(10);
    timings.note("id-2");
    clock.tick(10);
    expect(timings.settle("id-1")).toBe(20);
    expect(timings.settle("id-2")).toBe(10);
  });

  it("evicts entries older than the TTL — a late ack settles to null", () => {
    const clock = makeClock();
    const timings = new PendingAckTimings(10_000, PENDING_ACK_MAX_ENTRIES, clock.now);
    timings.note("stale");
    clock.tick(10_001);
    expect(timings.settle("stale")).toBeNull();
    expect(timings.size).toBe(0);
  });

  it("keeps an entry exactly at the TTL boundary", () => {
    const clock = makeClock();
    const timings = new PendingAckTimings(10_000, PENDING_ACK_MAX_ENTRIES, clock.now);
    timings.note("edge");
    clock.tick(10_000);
    expect(timings.settle("edge")).toBe(10_000);
  });

  it("sweeps expired entries on note, so an unacked burst cannot leak", () => {
    const clock = makeClock();
    const timings = new PendingAckTimings(10_000, PENDING_ACK_MAX_ENTRIES, clock.now);
    timings.note("old-1");
    timings.note("old-2");
    clock.tick(10_001);
    timings.note("fresh");
    expect(timings.size).toBe(1);
    expect(timings.settle("fresh")).toBe(0);
  });

  it("sweeps only the expired prefix, keeping fresher entries", () => {
    const clock = makeClock();
    const timings = new PendingAckTimings(10_000, PENDING_ACK_MAX_ENTRIES, clock.now);
    timings.note("old");
    clock.tick(6000);
    timings.note("newer");
    clock.tick(6000); // "old" is now 12s stale, "newer" only 6s
    timings.note("newest");
    expect(timings.size).toBe(2);
    expect(timings.settle("old")).toBeNull();
    expect(timings.settle("newer")).toBe(6000);
  });

  it("caps the map at maxEntries, evicting oldest-first", () => {
    const clock = makeClock();
    const timings = new PendingAckTimings(10_000, 3, clock.now);
    timings.note("a");
    clock.tick(1);
    timings.note("b");
    clock.tick(1);
    timings.note("c");
    clock.tick(1);
    timings.note("d"); // over the cap: "a" (oldest) is evicted
    expect(timings.size).toBe(3);
    expect(timings.settle("a")).toBeNull();
    expect(timings.settle("b")).toBe(2);
    expect(timings.settle("c")).toBe(1);
    expect(timings.settle("d")).toBe(0);
  });

  it("uses performance.now by default (constructor smoke)", () => {
    const timings = new PendingAckTimings();
    timings.note("real-clock");
    const elapsed = timings.settle("real-clock");
    expect(elapsed).not.toBeNull();
    expect(elapsed).toBeGreaterThanOrEqual(0);
  });
});

const ACTION_ID = "8b9ce2e6-9d0a-4f7e-9a76-1a2b3c4d5e6f";

/** Recording logger double for the two timing events. */
function makeTimingRecorder(): {
  logger: TimingLogger;
  events: Record<string, unknown>[];
} {
  const events: Record<string, unknown>[] = [];
  return {
    logger: {
      info: (obj) => {
        events.push(obj);
      },
    },
    events,
  };
}

describe("makeActionDispatcher — {evt:'action.timing'} per cluster write", () => {
  function makeHarness(sendResult = true): {
    dispatch: (write: Parameters<ReturnType<typeof makeActionDispatcher>>[0]) => void;
    sent: ActionFrame[];
    events: Record<string, unknown>[];
    timings: PendingAckTimings;
    clock: { now: () => number; tick: (ms: number) => void };
  } {
    const clock = makeClock();
    const sent: ActionFrame[] = [];
    const { logger, events } = makeTimingRecorder();
    const timings = new PendingAckTimings(PENDING_ACK_TTL_MS, PENDING_ACK_MAX_ENTRIES, clock.now);
    const dispatch = makeActionDispatcher({
      send: (frame) => {
        sent.push(frame);
        clock.tick(1.5); // the WS write itself takes time
        return sendResult;
      },
      logger,
      timings,
      newId: () => ACTION_ID,
      now: clock.now,
    });
    return { dispatch, sent, events, timings, clock };
  }

  it("sends the mapped action and logs elapsedMs keyed by the frame's id", () => {
    const { dispatch, sent, events } = makeHarness();
    dispatch({ endpoint: "power", cluster: "onOff", on: true, momentary: false });
    expect(sent).toEqual([{ v: 4, type: "action", id: ACTION_ID, name: "powerOn" }]);
    expect(events).toEqual([
      { evt: "action.timing", id: ACTION_ID, name: "powerOn", elapsedMs: 1.5 },
    ]);
  });

  it("notes a successful send in the pending map (ack latency measurable)", () => {
    const { dispatch, timings } = makeHarness();
    dispatch({ endpoint: "speaker", cluster: "levelControl", level: 127 });
    expect(timings.size).toBe(1);
  });

  it("does not note a dropped send — no ack can ever arrive for it", () => {
    const { dispatch, timings, events } = makeHarness(false);
    dispatch({ endpoint: "power", cluster: "onOff", on: false, momentary: false });
    expect(timings.size).toBe(0);
    // The action.timing line is still logged: the write DID happen.
    expect(events).toHaveLength(1);
  });

  it("dispatches playPause Off as dedicated pause", () => {
    const { dispatch, sent, events, timings } = makeHarness();
    dispatch({ endpoint: "playPause", cluster: "onOff", on: false });
    expect(sent).toEqual([{ v: 4, type: "action", id: ACTION_ID, name: "pause" }]);
    expect(events).toHaveLength(1);
    expect(timings.size).toBe(1);
  });

  it("does not send, time, or await an ack for reset-enabled custom Off", () => {
    const { dispatch, sent, events, timings } = makeHarness();
    dispatch({
      endpoint: "custom",
      key: "movie-mode",
      cluster: "onOff",
      on: false,
      resetAfterActivation: true,
    });
    expect(sent).toEqual([]);
    expect(events).toEqual([]);
    expect(timings.size).toBe(0);
  });
});

describe("makeAckTimingObserver — {evt:'ack.timing'} per acked send", () => {
  it("logs send->ack latency for a pending id, consuming the entry", () => {
    const clock = makeClock();
    const { logger, events } = makeTimingRecorder();
    const timings = new PendingAckTimings(PENDING_ACK_TTL_MS, PENDING_ACK_MAX_ENTRIES, clock.now);
    const observe = makeAckTimingObserver({ logger, timings });
    timings.note(ACTION_ID);
    clock.tick(42.0004);
    observe({ id: ACTION_ID, ok: true });
    expect(events).toEqual([{ evt: "ack.timing", id: ACTION_ID, ok: true, elapsedMs: 42 }]);
    observe({ id: ACTION_ID, ok: true }); // duplicate ack: entry consumed
    expect(events).toHaveLength(1);
  });

  it("carries ok:false through for a failed action's ack", () => {
    const clock = makeClock();
    const { logger, events } = makeTimingRecorder();
    const timings = new PendingAckTimings(PENDING_ACK_TTL_MS, PENDING_ACK_MAX_ENTRIES, clock.now);
    makeAckTimingObserver({ logger, timings });
    timings.note(ACTION_ID);
    clock.tick(5);
    makeAckTimingObserver({ logger, timings })({ id: ACTION_ID, ok: false });
    expect(events[0]).toMatchObject({ evt: "ack.timing", ok: false, elapsedMs: 5 });
  });

  it("stays silent for an unknown id", () => {
    const { logger, events } = makeTimingRecorder();
    const observe = makeAckTimingObserver({ logger, timings: new PendingAckTimings() });
    observe({ id: ACTION_ID, ok: true });
    expect(events).toEqual([]);
  });

  it("stays silent for an ack arriving after the 10 s TTL (evicted entry)", () => {
    const clock = makeClock();
    const { logger, events } = makeTimingRecorder();
    const timings = new PendingAckTimings(PENDING_ACK_TTL_MS, PENDING_ACK_MAX_ENTRIES, clock.now);
    const observe = makeAckTimingObserver({ logger, timings });
    timings.note(ACTION_ID);
    clock.tick(PENDING_ACK_TTL_MS + 1);
    observe({ id: ACTION_ID, ok: true });
    expect(events).toEqual([]);
    expect(timings.size).toBe(0);
  });
});
