/**
 * WebSocket client to the tray application (docs/BLUEPRINT.md §2.3).
 *
 * Uses Node 22's built-in global `WebSocket` (undici) — no production
 * dependency. Boundary behaviour per docs/ENGINEERING-STANDARDS.md: the tray
 * app being down is a boundary failure, so outbound frames are dropped with
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
 * - Inbound frames cross the trust boundary through `parseTrayFrame`;
 *   invalid frames are logged (one WARN each) and ignored, never thrown.
 */
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
    if (this.#state === "connected" && this.#ws !== undefined) {
      this.#ws.send(JSON.stringify(frame));
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
    const ws = new WebSocket(this.#options.url);
    this.#ws = ws;
    ws.addEventListener("open", () => {
      this.#handleOpen(ws);
    });
    ws.addEventListener("message", (event) => {
      // undici-types declares `MessageEvent.data` as `any`; downgrade it to
      // `unknown` so #handleMessage narrows it at the trust boundary.
      this.#handleMessage(ws, (event as { data: unknown }).data);
    });
    // Node 22's undici fires only "error" (no "close") when the connection
    // fails to establish — e.g. nothing listening — so both events must
    // schedule the reconnect; the settled flag keeps it to once per socket
    // when a failure fires both.
    let settled = false;
    const onDown = (): void => {
      if (!settled) {
        settled = true;
        this.#handleDown(ws);
      }
    };
    ws.addEventListener("close", onDown);
    ws.addEventListener("error", onDown);
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
    this.#setState("connected");
    this.#options.logger.info({ evt: "ipc.connected" }, "connected to tray app; hello sent");
  }

  #handleMessage(ws: WebSocket, data: unknown): void {
    if (ws !== this.#ws) {
      return;
    }
    if (typeof data !== "string") {
      this.#warnInvalidFrame("non-text message");
      return;
    }
    let json: unknown;
    try {
      json = JSON.parse(data);
    } catch {
      this.#warnInvalidFrame("not JSON");
      return;
    }
    const result = parseTrayFrame(json);
    if (!result.success) {
      this.#warnInvalidFrame("schema mismatch");
      return;
    }
    if (!this.#established) {
      // First authenticated tray frame — session established, backoff resets.
      this.#established = true;
      this.#attempt = 0;
    }
    this.#options.onFrame(result.data);
  }

  #warnInvalidFrame(reason: string): void {
    this.#options.logger.warn(
      { evt: "ipc.invalid-frame", reason },
      "ignoring invalid inbound frame",
    );
  }

  #handleDown(ws: WebSocket): void {
    if (ws !== this.#ws) {
      return; // stopped, or superseded by a newer socket
    }
    this.#ws = undefined;
    if (this.#state === "connected") {
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
