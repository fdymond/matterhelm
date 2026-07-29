/**
 * Pure diagnostics plumbing for the matter.js -> pino log forwarding and the
 * session-observability events (ADR-006 §1). Like `devices.ts`, this module
 * has NO runtime matter.js dependency so its logic unit-tests without loading
 * matter.js; `adapter.ts` (the only `@matter/*` import site) binds these pure
 * pieces to the real `Logger.destinations` / `server.events.sessions` APIs.
 *
 * The numeric log-level vocabulary mirrors matter.js's `LogLevel` constants
 * (@matter/general `dist/esm/log/LogLevel.js`: DEBUG=0, INFO=1, NOTICE=2,
 * WARN=3, ERROR=4, FATAL=5 — a stable public enum). The names in
 * {@link MATTER_LOG_LEVELS} are exactly the strings matter.js's `LogLevel()`
 * parser accepts, so `adapter.ts` can assign them to `Logger.level` /
 * `Logger.facilityLevels` verbatim.
 */

/**
 * matter.js log-level names (the `LogLevel()` string vocabulary), quietest
 * last. `config.ts` builds its `HTPC_BRIDGE_MATTER_LOG_LEVEL` /
 * `HTPC_BRIDGE_MATTER_LOG_FACILITIES` schemas from this list so the env
 * contract can never drift from what the adapter actually accepts.
 */
export const MATTER_LOG_LEVELS = ["debug", "info", "notice", "warn", "error", "fatal"] as const;

/** One matter.js log-level name (see {@link MATTER_LOG_LEVELS}). */
export type MatterLogLevel = (typeof MATTER_LOG_LEVELS)[number];

/** The pino level names a matter.js level can map onto (no trace/silent). */
export type MatterMappedPinoLevel = "debug" | "info" | "warn" | "error" | "fatal";

/**
 * Narrow structural logger surface the matter/ package logs through —
 * pino's `Logger` satisfies it. Mirrors `ipc/client.ts`'s `IpcLogger`
 * pattern: the composition root wires the real pino instance.
 */
export interface DiagnosticsLogger {
  debug(obj: Record<string, unknown>, msg: string): void;
  info(obj: Record<string, unknown>, msg: string): void;
  warn(obj: Record<string, unknown>, msg: string): void;
  error(obj: Record<string, unknown>, msg: string): void;
  fatal(obj: Record<string, unknown>, msg: string): void;
}

/**
 * Maps a matter.js numeric level to the pino level its events log at.
 * matter.js NOTICE ("elevated info") has no pino counterpart and maps to
 * `info`. Out-of-range input (a future matter.js level) clamps to the
 * nearest end rather than throwing — a log level must never crash logging.
 */
export function matterLevelToPinoLevel(level: number): MatterMappedPinoLevel {
  if (level <= 0) {
    return "debug";
  }
  switch (level) {
    case 1:
    case 2:
      return "info";
    case 3:
      return "warn";
    case 4:
      return "error";
    default:
      return "fatal";
  }
}

/**
 * The shape of a matter.js `Diagnostic.Message` this module consumes —
 * structural (no matter.js import), so the write path unit-tests with plain
 * objects. `adapter.ts` passes the real message through.
 */
export interface MatterLogMessage {
  level: number;
  facility: string;
}

/**
 * Builds the `write` half of the pino `LogDestination`: takes the
 * destination-formatted text (the adapter formats with matter.js's PLAIN
 * formatter over `message.values` only — no timestamp/level/facility
 * preamble, no ANSI) and emits one structured pino event per matter.js line.
 */
export function makeMatterLogWriter(
  logger: DiagnosticsLogger,
): (text: string, message: MatterLogMessage) => void {
  return (text, message) => {
    logger[matterLevelToPinoLevel(message.level)](
      { evt: "matter.log", facility: message.facility },
      text,
    );
  };
}

/**
 * Structural mirror of matter.js's `SessionsBehavior.Session` (the payload
 * of `server.events.sessions.opened/closed/subscriptionsChanged`) — only the
 * fields we log. Node/fabric ids arrive as bigints from matter.js.
 */
export interface MatterSessionInfo {
  name: string;
  nodeId: bigint | number;
  peerNodeId: bigint | number;
  fabric?: {
    fabricIndex: number;
    fabricId: bigint | number;
    rootVendorId: number;
    label: string;
  };
  isPeerActive: boolean;
  numberOfActiveSubscriptions: number;
}

/**
 * Flattens a session event's peer/fabric identity into pino-safe fields
 * (bigint ids become strings — pino's JSON serializer cannot emit bigint).
 * Deliberately identity-only: fabric/node ids and the fabric label, never
 * key material or session secrets (ADR-006 §1).
 */
export function sessionLogFields(session: MatterSessionInfo): Record<string, unknown> {
  return {
    session: session.name,
    nodeId: String(session.nodeId),
    peerNodeId: String(session.peerNodeId),
    ...(session.fabric === undefined
      ? {}
      : {
          fabricIndex: session.fabric.fabricIndex,
          fabricId: String(session.fabric.fabricId),
          rootVendorId: session.fabric.rootVendorId,
          fabricLabel: session.fabric.label,
        }),
    peerActive: session.isPeerActive,
    subscriptions: session.numberOfActiveSubscriptions,
  };
}

/**
 * Wires session/subscription lifecycle observability (ADR-006 §1) against a
 * plain-data event source. `adapter.ts` supplies the real
 * `server.events.sessions` observables through {@link MatterSessionEvents};
 * tests supply a double — the mapping itself is identical either way.
 *
 * Event vocabulary (what matter.js 0.17.7 exposes at node level):
 * - `opened`/`closed` (CASE sessions only; PASE is filtered upstream) ->
 *   `{evt:"matter.session", action:"established"|"closed", ...identity}`.
 * - `subscriptionAdded(subscription)` -> `{evt:"matter.subscription",
 *   action:"created", subscriptionId}`.
 * - `subscriptionsChanged(session)` fires on every add AND remove/expiry —
 *   there is no dedicated node-level "expired" event — so a shrinking
 *   `subscriptions` count in `action:"changed"` IS the expiry signal.
 */
export function wireSessionObservability(
  events: MatterSessionEvents,
  logger: DiagnosticsLogger,
): void {
  events.opened((session) => {
    logger.info(
      { evt: "matter.session", action: "established", ...sessionLogFields(session) },
      "matter session established",
    );
  });
  events.closed((session) => {
    logger.info(
      { evt: "matter.session", action: "closed", ...sessionLogFields(session) },
      "matter session closed",
    );
  });
  events.subscriptionAdded((subscription) => {
    logger.info(
      {
        evt: "matter.subscription",
        action: "created",
        subscriptionId: subscription.subscriptionId,
      },
      "matter subscription created",
    );
  });
  events.subscriptionsChanged((session) => {
    logger.info(
      { evt: "matter.subscription", action: "changed", ...sessionLogFields(session) },
      "matter subscription set changed",
    );
  });
}

/** Plain-data view of `server.events.sessions` (each field subscribes). */
export interface MatterSessionEvents {
  opened(cb: (session: MatterSessionInfo) => void): void;
  closed(cb: (session: MatterSessionInfo) => void): void;
  subscriptionAdded(cb: (subscription: { subscriptionId: number }) => void): void;
  subscriptionsChanged(cb: (session: MatterSessionInfo) => void): void;
}
