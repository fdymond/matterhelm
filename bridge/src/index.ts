/**
 * Composition root (docs/BLUEPRINT.md §2.1): config → ipc client → matter
 * bridge → run. Wiring only — pure decisions live in `mapping/`; matter.js
 * and `ws` stay behind `matter/adapter.ts` and `ipc/client.ts` respectively.
 *
 * Pairing-frame re-emission: {@link BridgeHandle.pairingCodes} is `null`
 * until the bridge is started and `null` again once commissioned (adapter.ts
 * semantics), so `maybeEmitPairing` is naturally a no-op both before start
 * and after commissioning — no separate "stop re-sending" flag is needed.
 * `IpcClient` exposes lifecycle via `onStateChange` rather than a dedicated
 * "connected to a fresh session" callback; its `"connected"` state fires
 * exactly once per socket-open+hello-send (client.ts), which is the closest
 * available signal to "reconnected" and is what this module re-sends on.
 */
import { randomUUID } from "node:crypto";

import { loadConfig } from "./config.js";
import type { Config } from "./config.js";
import { IpcClient } from "./ipc/client.js";
import type { IpcClientState } from "./ipc/client.js";
import { PROTOCOL_VERSION } from "./ipc/protocol.js";
import type { TrayFrame } from "./ipc/protocol.js";
import { makeLogger } from "./log.js";
import { stateFrameToSpeakerAttributes } from "./mapping/state.js";
import { createBridge } from "./matter/bridge.js";
import { PendingAckTimings, makeAckTimingObserver, makeActionDispatcher } from "./timing.js";

/** Hard-exit ceiling for graceful shutdown (BLUEPRINT §2.1 stdin tether). */
const SHUTDOWN_TIMEOUT_MS = 5000;

async function main(): Promise<void> {
  let config: Config;
  try {
    config = loadConfig();
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    process.stderr.write(`windows-google-home-matter: config error: ${message}\n`);
    process.exit(1);
  }

  const logger = makeLogger(config.logLevel);

  // `client` and the matter bridge each call into the other once running
  // (bridge -> client.send on a cluster write; client -> bridge on an
  // inbound tray frame), but neither construction needs the other's
  // instance — only the callbacks do, and those never fire before both
  // exist. `handlers` is the seam: the client is built against trampoline
  // callbacks now, and the real handlers (closing over `bridgeHandle`, a
  // plain `const` below) are installed once it exists.
  const handlers: {
    onFrame: (frame: TrayFrame) => void;
    onStateChange: (state: IpcClientState) => void;
  } = {
    onFrame: () => undefined,
    onStateChange: () => undefined,
  };

  const client = new IpcClient({
    // Must be the literal host "localhost", not 127.0.0.1: the tray app's
    // HttpListener registers the http://localhost:{port}/ prefix (ADR-003 —
    // the only admin-free loopback prefix) and rejects upgrades whose Host
    // header names any other form. Still loopback-only either way.
    url: `ws://localhost:${String(config.ipcPort)}`,
    token: config.ipcToken,
    logger,
    onFrame: (frame) => {
      handlers.onFrame(frame);
    },
    onStateChange: (state) => {
      handlers.onStateChange(state);
    },
  });

  // ADR-006 §1 per-action timing: cluster write -> WS send elapsed, and
  // send -> ack latency, both keyed by the action frame's uuid `id`.
  const ackTimings = new PendingAckTimings();
  const observeAck = makeAckTimingObserver({ logger, timings: ackTimings });

  const bridgeHandle = await createBridge({
    storageDir: config.storageDir,
    endpoints: config.endpoints,
    momentaryResetMs: config.momentaryResetMs,
    logger,
    matterLogLevel: config.matterLogLevel,
    matterLogFacilities: config.matterLogFacilities,
    ...(config.matterPort === undefined ? {} : { port: config.matterPort }),
    ...(config.mdnsInterface === undefined ? {} : { mdnsInterface: config.mdnsInterface }),
    // S10-4: identity + vendor/product, each defaulting inside bridge.ts when
    // the tray app sends nothing (keeps a standalone sidecar run unchanged).
    ...(config.uniqueIdSeed === undefined ? {} : { uniqueIdSeed: config.uniqueIdSeed }),
    ...(config.vendorId === undefined ? {} : { vendorId: config.vendorId }),
    ...(config.productId === undefined ? {} : { productId: config.productId }),
    onClusterWrite: makeActionDispatcher({
      send: (frame) => client.send(frame),
      logger,
      timings: ackTimings,
      newId: randomUUID,
    }),
  });

  // One line per constructed endpoint: the audit trail that the ADR-004
  // config produced exactly the intended endpoint set (disabled built-ins
  // never appear here — they were never constructed).
  for (const endpoint of bridgeHandle.endpoints) {
    logger.info(
      { evt: "matter.endpoint", id: endpoint.id, kind: endpoint.kind, name: endpoint.name },
      "endpoint constructed",
    );
  }

  function maybeEmitPairing(): void {
    const codes = bridgeHandle.pairingCodes;
    if (codes === null) {
      return; // not started yet, or already commissioned (adapter.ts)
    }
    client.send({ v: PROTOCOL_VERSION, type: "pairing", ...codes });
  }

  // ADR-004: with the speaker endpoint disabled, tray state frames stay
  // tolerated but apply to nothing — logged at debug exactly once.
  let speakerDisabledLogged = false;

  handlers.onFrame = (frame) => {
    if (frame.type === "state") {
      if (!config.endpoints.speaker.enabled) {
        if (!speakerDisabledLogged) {
          speakerDisabledLogged = true;
          logger.debug(
            { evt: "ipc.state.ignored" },
            "speaker endpoint disabled; ignoring tray state frames",
          );
        }
        return;
      }
      const attrs = stateFrameToSpeakerAttributes(frame);
      logger.debug(
        { evt: "ipc.state", volume: frame.volume, muted: frame.muted, ...attrs },
        "applying tray state to speaker endpoint",
      );
      void bridgeHandle.setSpeakerState(attrs.currentLevel, attrs.onOff);
      return;
    }
    logger.debug(
      { evt: "ipc.ack", id: frame.id, ok: frame.ok, error: frame.ok ? undefined : frame.error },
      "tray app ack",
    );
    observeAck(frame);
  };
  handlers.onStateChange = (state) => {
    if (state === "connected") {
      maybeEmitPairing();
    }
  };

  bridgeHandle.onCommissionedChange((commissioned) => {
    logger.info(
      { evt: "matter.commissioned", commissioned },
      commissioned ? "controller commissioned" : "controller removed",
    );
  });

  client.start();
  await bridgeHandle.start();
  maybeEmitPairing();
  logger.info({ evt: "bridge.started", ipcPort: config.ipcPort }, "bridge started");

  let shuttingDown = false;
  function shutdown(reason: string): void {
    if (shuttingDown) {
      return;
    }
    shuttingDown = true;
    logger.info({ evt: "shutdown", reason }, "shutting down");
    const hardExit = setTimeout(() => {
      logger.error({ evt: "shutdown.timeout" }, "graceful shutdown timed out; forcing exit");
      process.exit(1);
    }, SHUTDOWN_TIMEOUT_MS);
    hardExit.unref();
    client.stop();
    void bridgeHandle
      .close()
      .then(() => {
        clearTimeout(hardExit);
        process.exit(0);
      })
      .catch((err: unknown) => {
        logger.error({ evt: "shutdown.error", err: String(err) }, "error while closing the bridge");
        clearTimeout(hardExit);
        process.exit(1);
      });
  }

  // The tray app supervisor tethers the child via stdin: closing its end of
  // the pipe (or the process dying) is our signal to self-terminate even if
  // the parent never sends SIGINT (BLUEPRINT §2.1).
  process.stdin.on("end", () => {
    shutdown("stdin-end");
  });
  process.stdin.on("close", () => {
    shutdown("stdin-close");
  });
  process.stdin.resume();
  process.on("SIGINT", () => {
    shutdown("SIGINT");
  });
}

void main();
