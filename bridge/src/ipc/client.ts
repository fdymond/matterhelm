/**
 * WebSocket client to the tray application (docs/BLUEPRINT.md §2.3).
 *
 * Uses `ws` because Node 22's built-in Undici WebSocket forbids callers from
 * sending RFC policy close code 1008. Boundary behaviour per
 * docs/ENGINEERING-STANDARDS.md: the tray app being down is a boundary failure,
 * so outbound frames are dropped with
 * exactly ONE structured WARN per disconnected episode (an episode ends when
 * the socket next opens), and the client never throws on the send path.
 *
 * Connection lifecycle:
 * - On socket open the `hello` frame is sent before anything else; the tray
 *   app is the auth authority, and a reject is just a close — the client
 *   keeps retrying with jittered exponential backoff.
 * - Backoff: base 500 ms doubling to a 30 s cap, then ±25 % jitter (so the
 *   worst-case wait is 37.5 s). The attempt counter resets once a session is
 *   *established* — defined as the first valid tray frame received after
 *   `hello` (the tray app sends `state` on connect, §2.3). Resetting on bare
 *   socket-open would let a hello-rejecting server pin retries at the base
 *   delay forever; requiring an authenticated frame keeps rejects backing
 *   off to the cap.
 * - Inbound frames cross the trust boundary through `parseTrayFrame`; the
 *   first invalid frame closes that connection with policy code 1008. The
 *   normal bounded reconnect path then recovers against a healthy peer.
 * - A policy close before the first valid tray frame is the stale-peer
 *   signature: the spawning tray supplied the session token, so repeated
 *   rejection of the well-formed hello diagnoses tray/sidecar version skew.
 *   It is logged once, then summarized every ten rejects.
 */
import { WebSocket } from "ws";

import { PROTOCOL_VERSION, parseTrayFrame } from "./protocol.js";
import type {
  ActionFrame,
  HelloFrame,
  MatterStatusFrame,
  PairingFrame,
  TrayFrame,
} from "./protocol.js";

/** Narrow pino-compatible logger surface; the composition root wires pino. */
export interface IpcLogger {
  info(obj: Record<string, unknown>, msg: string): void;
  warn(obj: Record<string, unknown>, msg: string): void;
}

/** Every frame the client sends on behalf of callers (`hello` is internal). */
export type OutboundFrame = ActionFrame | PairingFrame | MatterStatusFrame;

/** Observable lifecycle state (also the seam integration tests wait on). */
export type IpcClientState = "idle" | "connecting" | "connected" | "waiting" | "stopped";

/** Reconnect backoff shape; defaults are {@link DEFAULT_BACKOFF}. */
export interface BackoffOptions {
  /** Delay before the first retry; doubles each failed attempt. */
  baseMs: number;
  /** Upper bound on the pre-jitter delay. */
  capMs: number;
}

export const DEFAULT_BACKOFF: BackoffOptions = { baseMs: 500, capMs: 30_000 };

/** Jitter fraction: the delay is scaled uniformly within ±25 %. */
const JITTER = 0.25;

/**
 * Maximum queued WebSocket payload before the peer is treated as unhealthy.
 * Two MiB absorbs short tray-app stalls while bounding the sidecar's queue far
 * below the demonstrated 16 MiB growth.
 */
const MAX_BUFFERED_BYTES = 2 * 1024 * 1024;

/** Match the tray server's inbound frame cap and reject compressed IPC frames. */
const MAX_PAYLOAD_BYTES = 64 * 1024;

/** Count-based summary cadence once a stale peer repeatedly rejects hello. */
const VERSION_REJECTION_SUMMARY_EVERY = 10;

/**
 * Pure backoff schedule: `min(baseMs * 2^attempt, capMs)` scaled by a uniform
 * jitter factor in [1-JITTER, 1+JITTER]. `random` is injectable for tests.
 */
export function backoffDelayMs(
  attempt: number,
  { baseMs, capMs }: BackoffOptions,
  random: () => number = Math.random,
): number {
  const capped = Math.min(baseMs * 2 ** attempt, capMs);
  return Math.round(capped * (1 - JITTER + 2 * JITTER * random()));
}

export interface IpcClientOptions {
  /** ws:// URL of the tray app's loopback IPC server. */
  url: string;
  /** Session token (from env; never logged). */
  token: string;
  logger: IpcLogger;
  /** Called with every validated inbound tray frame. */
  onFrame: (frame: TrayFrame) => void;
  /** Called on every lifecycle state change. */
  onStateChange?: (state: IpcClientState) => void;
  /** Override backoff constants (tests); production uses the defaults. */
  backoff?: BackoffOptions;
  /** Socket construction seam for boundary unit tests. */
  createWebSocket?: (url: string) => WebSocket;
}

/**
 * IPC client to the tray app: token hello, jittered-backoff reconnect,
 * drop-with-one-WARN degradation while the peer is absent.
 */
export class IpcClient {
  readonly #options: IpcClientOptions;
  readonly #backoff: BackoffOptions;
  #state: IpcClientState = "idle";
  #ws: WebSocket | undefined;
  #attempt = 0;
  #established = false;
  #dropWarned = false;
  #backpressureWarned = false;
  #versionRejections = 0;
  #invalidFrameClose: WebSocket | undefined;
  #previousCloseWasLocalReject = false;
  #reconnectTimer: ReturnType<typeof setTimeout> | undefined;

  constructor(options: IpcClientOptions) {
    this.#options = options;
    this.#backoff = options.backoff ?? DEFAULT_BACKOFF;
  }

  get state(): IpcClientState {
    return this.#state;
  }

  /** Begins connecting. Call once; calling again is an internal bug. */
  start(): void {
    if (this.#state !== "idle") {
      throw new Error(`IpcClient.start() called in state "${this.#state}"`);
    }
    this.#connect();
  }

  /**
   * Sends a frame if connected. While disconnected (or stopped) the frame is
   * dropped and `false` returned; the first drop of each disconnected episode
   * logs one WARN, later drops are silent until the socket next opens. Never
   * throws — Matter writes must survive the tray app being away (§2.3).
   */
  send(frame: OutboundFrame): boolean {
    if (
      this.#state === "connected" &&
      this.#ws !== undefined &&
      this.#ws.readyState === WebSocket.OPEN
    ) {
      if (this.#invalidFrameClose === this.#ws || this.#backpressureWarned) {
        return false;
      }
      const payload = JSON.stringify(frame);
      const frameBytes = Buffer.byteLength(payload);
      if (this.#ws.bufferedAmount + frameBytes > MAX_BUFFERED_BYTES) {
        this.#backpressureWarned = true;
        this.#options.logger.warn(
          {
            evt: "ipc.backpressure",
            bufferedBytes: this.#ws.bufferedAmount,
            frameBytes,
            maxBufferedBytes: MAX_BUFFERED_BYTES,
          },
          "tray app is not reading IPC frames; closing unhealthy connection",
        );
        this.#ws.close(1013, "IPC peer backpressure");
        return false;
      }
      this.#ws.send(payload);
      return true;
    }
    if (!this.#dropWarned) {
      this.#dropWarned = true;
      this.#options.logger.warn(
        { evt: "ipc.drop", frameType: frame.type },
        "tray app not connected; dropping outbound frames until reconnect",
      );
    }
    return false;
  }

  /** Closes the socket and cancels any pending reconnect. Idempotent. */
  stop(): void {
    if (this.#state === "stopped") {
      return;
    }
    this.#setState("stopped");
    if (this.#reconnectTimer !== undefined) {
      clearTimeout(this.#reconnectTimer);
      this.#reconnectTimer = undefined;
    }
    if (this.#ws !== undefined) {
      const ws = this.#ws;
      this.#ws = undefined;
      ws.close(1000, "sidecar shutting down");
    }
  }

  #setState(state: IpcClientState): void {
    this.#state = state;
    this.#options.onStateChange?.(state);
  }

  #connect(): void {
    this.#setState("connecting");
    this.#established = false;
    const ws =
      this.#options.createWebSocket?.(this.#options.url) ??
      // IPC frames are small JSON objects; mirror the tray cap and avoid an
      // unnecessary compression surface on this loopback-only connection.
      new WebSocket(this.#options.url, {
        maxPayload: MAX_PAYLOAD_BYTES,
        perMessageDeflate: false,
      });
    this.#ws = ws;
    ws.addEventListener("open", () => {
      this.#handleOpen(ws);
    });
    ws.addEventListener("message", (event) => {
      this.#handleMessage(ws, event.data);
    });
    // Some WebSocket implementations fire only "error" (no "close") when a
    // connection cannot be established, so either event settles the attempt.
    let settled = false;
    const onDown = (close?: { code: number; reason: string }): void => {
      if (!settled) {
        settled = true;
        this.#handleDown(ws, close);
      }
    };
    ws.addEventListener("close", (event) => {
      onDown({ code: event.code, reason: event.reason });
    });
    ws.addEventListener("error", () => {
      onDown();
    });
  }

  #handleOpen(ws: WebSocket): void {
    if (ws !== this.#ws) {
      return; // stale socket; stop() already disowned it
    }
    const hello: HelloFrame = {
      v: PROTOCOL_VERSION,
      type: "hello",
      token: this.#options.token,
      protocol: 1,
    };
    ws.send(JSON.stringify(hello)); // first frame, before any caller send()
    this.#dropWarned = false; // new episode begins at the next disconnect
    this.#backpressureWarned = false;
    this.#setState("connected");
    if (this.#versionRejections === 0) {
      this.#options.logger.info({ evt: "ipc.connected" }, "connected to tray app; hello sent");
    }
  }

  #handleMessage(ws: WebSocket, data: unknown): void {
    if (ws !== this.#ws || ws.readyState !== WebSocket.OPEN) {
      return;
    }
    if (typeof data !== "string") {
      this.#closeInvalidFrame(ws, "non-text message");
      return;
    }
    let json: unknown;
    try {
      json = JSON.parse(data);
    } catch {
      this.#closeInvalidFrame(ws, "not JSON");
      return;
    }
    const result = parseTrayFrame(json);
    if (!result.success) {
      this.#closeInvalidFrame(ws, "schema mismatch");
      return;
    }
    if (!this.#established) {
      // First authenticated tray frame — session established; backoff normally resets.
      // After locally rejecting the preceding peer, retain the attempt count:
      // one valid frame followed by an invalid one must still back off.
      this.#established = true;
      if (!this.#previousCloseWasLocalReject) {
        this.#attempt = 0;
      }
      this.#previousCloseWasLocalReject = false;
      if (this.#versionRejections > 0) {
        this.#options.logger.info(
          { evt: "ipc.version-mismatch.resolved", rejectedHandshakes: this.#versionRejections },
          "tray accepted the IPC handshake; version-mismatch condition cleared",
        );
        this.#versionRejections = 0;
      }
    }
    this.#options.onFrame(result.data);
  }

  #closeInvalidFrame(ws: WebSocket, reason: string): void {
    this.#invalidFrameClose = ws;
    this.#options.logger.warn(
      { evt: "ipc.invalid-frame", reason },
      "closing connection after invalid inbound frame",
    );
    ws.close(1008, "invalid inbound frame");
  }

  #noteVersionRejection(close: { code: number; reason: string }): void {
    this.#versionRejections += 1;
    if (
      this.#versionRejections !== 1 &&
      this.#versionRejections % VERSION_REJECTION_SUMMARY_EVERY !== 0
    ) {
      return;
    }
    const summary = this.#versionRejections > 1;
    this.#options.logger.warn(
      {
        evt: summary ? "ipc.version-mismatch.summary" : "ipc.version-mismatch",
        diagnosis: "tray-sidecar-version-mismatch",
        rejectedHandshakes: this.#versionRejections,
        sidecarFrameVersion: PROTOCOL_VERSION,
        handshakeProtocol: 1,
        closeCode: close.code,
        ...(close.reason === "" ? {} : { closeReason: close.reason }),
      },
      summary
        ? "IPC VERSION MISMATCH persists; tray keeps rejecting this sidecar hello; update both components to the same MatterHelm release"
        : "IPC VERSION MISMATCH: tray rejected this sidecar hello; update tray and sidecar to the same MatterHelm release",
    );
  }

  #handleDown(ws: WebSocket, close?: { code: number; reason: string }): void {
    if (ws !== this.#ws) {
      return; // stopped, or superseded by a newer socket
    }
    this.#ws = undefined;
    const locallyRejectedInvalidFrame = this.#invalidFrameClose === ws;
    if (locallyRejectedInvalidFrame) {
      this.#invalidFrameClose = undefined;
    }
    this.#previousCloseWasLocalReject = locallyRejectedInvalidFrame;
    const versionRejected =
      !locallyRejectedInvalidFrame && !this.#established && close?.code === 1008;
    if (versionRejected) {
      this.#noteVersionRejection(close);
    } else if (this.#state === "connected") {
      this.#options.logger.info({ evt: "ipc.disconnected" }, "tray app connection closed");
    }
    const delay = backoffDelayMs(this.#attempt, this.#backoff);
    this.#attempt += 1;
    this.#setState("waiting");
    this.#reconnectTimer = setTimeout(() => {
      this.#reconnectTimer = undefined;
      this.#connect();
    }, delay);
  }
}
