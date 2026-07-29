/**
 * Specification tests for the pure diagnostics plumbing (ADR-006 §1).
 * matter/adapter.ts binds these pieces to the real matter.js `Logger` /
 * `server.events.sessions` APIs; the doubles here exercise exactly the same
 * mapping code, so what is asserted here is what the live wiring emits.
 */
import { describe, expect, it } from "vitest";

import {
  MATTER_LOG_LEVELS,
  makeMatterLogWriter,
  matterLevelToPinoLevel,
  sessionLogFields,
  wireSessionObservability,
} from "./diagnostics.js";
import type { DiagnosticsLogger, MatterSessionEvents, MatterSessionInfo } from "./diagnostics.js";

/** Recording logger double satisfying the DiagnosticsLogger seam. */
function makeRecorder(): {
  logger: DiagnosticsLogger;
  calls: { level: string; obj: Record<string, unknown>; msg: string }[];
} {
  const calls: { level: string; obj: Record<string, unknown>; msg: string }[] = [];
  const record =
    (level: string) =>
    (obj: Record<string, unknown>, msg: string): void => {
      calls.push({ level, obj, msg });
    };
  return {
    logger: {
      debug: record("debug"),
      info: record("info"),
      warn: record("warn"),
      error: record("error"),
      fatal: record("fatal"),
    },
    calls,
  };
}

describe("matterLevelToPinoLevel — matter.js LogLevel -> pino level", () => {
  it.each([
    [0, "debug"], // LogLevel.DEBUG
    [1, "info"], // LogLevel.INFO
    [2, "info"], // LogLevel.NOTICE (no pino counterpart)
    [3, "warn"], // LogLevel.WARN
    [4, "error"], // LogLevel.ERROR
    [5, "fatal"], // LogLevel.FATAL
  ] as const)("maps matter.js level %d to pino %s", (matterLevel, pinoLevel) => {
    expect(matterLevelToPinoLevel(matterLevel)).toBe(pinoLevel);
  });

  it("clamps out-of-range levels instead of throwing (logging must not crash)", () => {
    expect(matterLevelToPinoLevel(-1)).toBe("debug");
    expect(matterLevelToPinoLevel(6)).toBe("fatal");
  });

  it("covers the whole MATTER_LOG_LEVELS vocabulary (one name per numeric level)", () => {
    expect(MATTER_LOG_LEVELS).toHaveLength(6);
  });
});

describe("makeMatterLogWriter — LogDestination write -> pino event", () => {
  it("emits {evt:'matter.log', facility} with the text as the message", () => {
    const { logger, calls } = makeRecorder();
    const write = makeMatterLogWriter(logger);
    write("Session established", { level: 1, facility: "SessionManager" });
    expect(calls).toEqual([
      {
        level: "info",
        obj: { evt: "matter.log", facility: "SessionManager" },
        msg: "Session established",
      },
    ]);
  });

  it("routes each matter.js level to its pino method", () => {
    const { logger, calls } = makeRecorder();
    const write = makeMatterLogWriter(logger);
    for (let level = 0; level <= 5; level++) {
      write(`line ${String(level)}`, { level, facility: "Test" });
    }
    expect(calls.map((call) => call.level)).toEqual([
      "debug",
      "info",
      "info",
      "warn",
      "error",
      "fatal",
    ]);
  });
});

const SESSION: MatterSessionInfo = {
  name: "secure/32161",
  nodeId: 1n,
  peerNodeId: 8797175n,
  fabric: {
    fabricIndex: 1,
    fabricId: 4386700219917201510n,
    rootVendorId: 24582,
    label: "Google Home",
  },
  isPeerActive: true,
  numberOfActiveSubscriptions: 2,
};

describe("sessionLogFields — peer identity as pino-safe fields", () => {
  it("stringifies bigint ids and flattens fabric identity (never secrets)", () => {
    expect(sessionLogFields(SESSION)).toEqual({
      session: "secure/32161",
      nodeId: "1",
      peerNodeId: "8797175",
      fabricIndex: 1,
      fabricId: "4386700219917201510",
      rootVendorId: 24582,
      fabricLabel: "Google Home",
      peerActive: true,
      subscriptions: 2,
    });
  });

  it("omits fabric fields for a session without fabric information", () => {
    const bare = { ...SESSION };
    delete bare.fabric;
    const fields = sessionLogFields(bare);
    expect(fields).toEqual({
      session: "secure/32161",
      nodeId: "1",
      peerNodeId: "8797175",
      peerActive: true,
      subscriptions: 2,
    });
    expect(Object.hasOwn(fields, "fabricId")).toBe(false);
  });
});

describe("wireSessionObservability — session lifecycle -> pino events", () => {
  /** Event-source double mirroring server.events.sessions (adapter.ts). */
  function makeSource(): {
    events: MatterSessionEvents;
    fire: {
      opened: (session: MatterSessionInfo) => void;
      closed: (session: MatterSessionInfo) => void;
      subscriptionAdded: (subscription: { subscriptionId: number }) => void;
      subscriptionsChanged: (session: MatterSessionInfo) => void;
    };
  } {
    const handlers = {
      opened: [] as ((session: MatterSessionInfo) => void)[],
      closed: [] as ((session: MatterSessionInfo) => void)[],
      subscriptionAdded: [] as ((subscription: { subscriptionId: number }) => void)[],
      subscriptionsChanged: [] as ((session: MatterSessionInfo) => void)[],
    };
    return {
      events: {
        opened: (cb) => handlers.opened.push(cb),
        closed: (cb) => handlers.closed.push(cb),
        subscriptionAdded: (cb) => handlers.subscriptionAdded.push(cb),
        subscriptionsChanged: (cb) => handlers.subscriptionsChanged.push(cb),
      },
      fire: {
        opened: (session) => {
          handlers.opened.forEach((cb) => {
            cb(session);
          });
        },
        closed: (session) => {
          handlers.closed.forEach((cb) => {
            cb(session);
          });
        },
        subscriptionAdded: (subscription) => {
          handlers.subscriptionAdded.forEach((cb) => {
            cb(subscription);
          });
        },
        subscriptionsChanged: (session) => {
          handlers.subscriptionsChanged.forEach((cb) => {
            cb(session);
          });
        },
      },
    };
  }

  it("logs {evt:'matter.session', action:'established'} on session open", () => {
    const { logger, calls } = makeRecorder();
    const source = makeSource();
    wireSessionObservability(source.events, logger);
    source.fire.opened(SESSION);
    expect(calls).toEqual([
      {
        level: "info",
        obj: { evt: "matter.session", action: "established", ...sessionLogFields(SESSION) },
        msg: "matter session established",
      },
    ]);
  });

  it("logs {evt:'matter.session', action:'closed'} on session close", () => {
    const { logger, calls } = makeRecorder();
    const source = makeSource();
    wireSessionObservability(source.events, logger);
    source.fire.closed(SESSION);
    expect(calls[0]?.obj).toMatchObject({ evt: "matter.session", action: "closed" });
  });

  it("logs {evt:'matter.subscription', action:'created'} with the subscription id", () => {
    const { logger, calls } = makeRecorder();
    const source = makeSource();
    wireSessionObservability(source.events, logger);
    source.fire.subscriptionAdded({ subscriptionId: 4059 });
    expect(calls).toEqual([
      {
        level: "info",
        obj: { evt: "matter.subscription", action: "created", subscriptionId: 4059 },
        msg: "matter subscription created",
      },
    ]);
  });

  it("logs {evt:'matter.subscription', action:'changed'} with the new count (expiry signal)", () => {
    const { logger, calls } = makeRecorder();
    const source = makeSource();
    wireSessionObservability(source.events, logger);
    source.fire.subscriptionsChanged({ ...SESSION, numberOfActiveSubscriptions: 0 });
    expect(calls[0]?.obj).toMatchObject({
      evt: "matter.subscription",
      action: "changed",
      subscriptions: 0,
    });
  });
});
