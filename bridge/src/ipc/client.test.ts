/**
 * Integration tests for the IPC client (S1-4) against a real `ws` mock tray
 * server on an ephemeral loopback port.
 *
 * Timer strategy: reconnect backoff runs on `setTimeout`, so backoff tests
 * fake ONLY setTimeout/clearTimeout and advance virtual time while socket
 * I/O keeps flowing on the real event loop (drained via `setImmediate`
 * turns — never faked, never slept). Event waits carry a real-time safety
 * timeout via `AbortSignal.timeout`, which runs on Node-internal timers that
 * fake timers cannot freeze.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { WebSocket, WebSocketServer } from "ws";
import type { WebSocket as ServerSocket, RawData } from "ws";

import { backoffDelayMs, DEFAULT_BACKOFF, IpcClient } from "./client.js";
import type { BackoffOptions, IpcClientState, IpcLogger, OutboundFrame } from "./client.js";
import { parseSidecarFrame } from "./protocol.js";
import type { TrayFrame } from "./protocol.js";

const TOKEN = "test-session-token";
const UUID = "123e4567-e89b-12d3-a456-426614174000";
const STATE_ON_CONNECT = { v: 5, type: "state", volume: 40, muted: false };

/** Rejects `promise` after 5 real seconds even under fake timers. */
function withTimeout<T>(promise: Promise<T>, what: string): Promise<T> {
  const signal = AbortSignal.timeout(5_000);
  return new Promise<T>((resolve, reject) => {
    signal.addEventListener("abort", () => {
      reject(new Error(`timed out waiting for: ${what}`));
    });
    promise.then(resolve, reject);
  });
}

/** Event-driven condition waiting: `notify()` re-checks every pending `until`. */
class Waiter {
  readonly #checks = new Set<() => void>();

  notify(): void {
    for (const check of [...this.#checks]) {
      check();
    }
  }

  async until(cond: () => boolean, what: string): Promise<void> {
    if (cond()) {
      return;
    }
    await withTimeout(
      new Promise<void>((resolve) => {
        const check = (): void => {
          if (cond()) {
            this.#checks.delete(check);
            resolve();
          }
        };
        this.#checks.add(check);
      }),
      what,
    );
  }
}

/** Spins the real event loop so in-flight socket I/O settles (no sleeps). */
async function drainIo(turns = 200): Promise<void> {
  for (let i = 0; i < turns; i += 1) {
    await new Promise<void>((resolve) => {
      setImmediate(resolve);
    });
  }
}

/**
 * Mock tray-app server. On a valid `hello` with the expected token it sends
 * {@link STATE_ON_CONNECT} (the tray app reports state on connect, §2.3);
 * in `"reject"` mode (or on a bad token) it closes the socket instead.
 */
class MockTrayServer {
  readonly frames: unknown[] = [];
  readonly closes: { code: number; reason: string }[] = [];
  connections = 0;
  mode: "accept" | "reject" = "accept";
  #wss: WebSocketServer | undefined;
  #lastSocket: ServerSocket | undefined;
  readonly #sockets = new Set<ServerSocket>();
  readonly #waiter: Waiter;

  constructor(waiter: Waiter) {
    this.#waiter = waiter;
  }

  async listen(port = 0): Promise<number> {
    const wss = new WebSocketServer({ host: "127.0.0.1", port });
    this.#wss = wss;
    await withTimeout(
      new Promise<void>((resolve, reject) => {
        wss.once("listening", resolve);
        wss.once("error", reject);
      }),
      "mock server listening",
    );
    wss.on("connection", (socket) => {
      this.connections += 1;
      this.#sockets.add(socket);
      this.#lastSocket = socket;
      socket.on("close", (code, reason) => {
        this.closes.push({ code, reason: reason.toString("utf8") });
        this.#sockets.delete(socket);
        this.#waiter.notify();
      });
      socket.on("message", (data: RawData) => {
        this.#handleMessage(socket, data);
      });
      this.#waiter.notify();
    });
    const address = wss.address();
    if (address === null || typeof address === "string") {
      throw new Error("mock server has no bound port");
    }
    return address.port;
  }

  #handleMessage(socket: ServerSocket, data: RawData): void {
    if (!Buffer.isBuffer(data)) {
      throw new Error("mock server expected a single text frame");
    }
    // Mock-server-only shortcut: test inputs are known-good JSON.
    const frame: unknown = JSON.parse(data.toString("utf8"));
    this.frames.push(frame);
    const parsed = parseSidecarFrame(frame);
    if (parsed.success && parsed.data.type === "hello") {
      if (this.mode === "reject" || parsed.data.token !== TOKEN) {
        socket.close(1008, "auth rejected");
      } else {
        socket.send(JSON.stringify(STATE_ON_CONNECT));
      }
    }
    this.#waiter.notify();
  }

  sendRaw(text: string): void {
    if (this.#lastSocket === undefined) {
      throw new Error("no client connected");
    }
    this.#lastSocket.send(text);
  }

  sendBinary(bytes: Buffer): void {
    if (this.#lastSocket === undefined) {
      throw new Error("no client connected");
    }
    this.#lastSocket.send(bytes);
  }

  /** Terminates live connections but keeps listening (simulated crash). */
  closeClients(): void {
    for (const socket of this.#sockets) {
      socket.terminate();
    }
  }

  async close(): Promise<void> {
    const wss = this.#wss;
    if (wss === undefined) {
      return;
    }
    this.#wss = undefined;
    this.closeClients();
    await new Promise<void>((resolve) => {
      wss.close(() => {
        resolve();
      });
    });
  }
}

interface LogCall {
  obj: Record<string, unknown>;
  msg: string;
}

function makeLogger(): { logger: IpcLogger; calls: { info: LogCall[]; warn: LogCall[] } } {
  const calls = { info: [] as LogCall[], warn: [] as LogCall[] };
  const logger: IpcLogger = {
    info: (obj, msg) => calls.info.push({ obj, msg }),
    warn: (obj, msg) => calls.warn.push({ obj, msg }),
  };
  return { logger, calls };
}

const dropWarns = (calls: { warn: LogCall[] }): LogCall[] =>
  calls.warn.filter((c) => c.obj.evt === "ipc.drop");
const invalidFrameWarns = (calls: { warn: LogCall[] }): LogCall[] =>
  calls.warn.filter((c) => c.obj.evt === "ipc.invalid-frame");
const versionMismatchWarns = (calls: { warn: LogCall[] }): LogCall[] =>
  calls.warn.filter(
    (c) => c.obj.evt === "ipc.version-mismatch" || c.obj.evt === "ipc.version-mismatch.summary",
  );
const count = (states: IpcClientState[], state: IpcClientState): number =>
  states.filter((s) => s === state).length;

const playPause: OutboundFrame = { v: 5, type: "action", id: UUID, name: "playPause" };

describe("backoffDelayMs", () => {
  it("doubles per attempt from the 500 ms base with neutral jitter", () => {
    const mid = (): number => 0.5;
    expect(backoffDelayMs(0, DEFAULT_BACKOFF, mid)).toBe(500);
    expect(backoffDelayMs(1, DEFAULT_BACKOFF, mid)).toBe(1000);
    expect(backoffDelayMs(2, DEFAULT_BACKOFF, mid)).toBe(2000);
  });

  it("caps the pre-jitter delay at 30 s", () => {
    expect(backoffDelayMs(20, DEFAULT_BACKOFF, () => 0.5)).toBe(30_000);
  });

  it("applies at most ±25 % jitter", () => {
    expect(backoffDelayMs(0, DEFAULT_BACKOFF, () => 0)).toBe(375);
    expect(backoffDelayMs(0, DEFAULT_BACKOFF, () => 1)).toBe(625);
  });
});

describe("IpcClient backpressure", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("logs once and closes a connection whose projected queue exceeds 2 MiB", () => {
    class FakeWebSocket extends EventTarget {
      static latest: FakeWebSocket | undefined;
      bufferedAmount = 0;
      readyState: number = WebSocket.OPEN;
      readonly sent: string[] = [];
      readonly closes: { code?: number; reason?: string }[] = [];
      readonly url: string;

      constructor(url: string) {
        super();
        this.url = url;
        FakeWebSocket.latest = this;
      }

      send(payload: string): void {
        this.sent.push(payload);
      }

      close(code?: number, reason?: string): void {
        this.closes.push({
          ...(code === undefined ? {} : { code }),
          ...(reason === undefined ? {} : { reason }),
        });
      }
    }
    const { logger, calls } = makeLogger();
    const c = new IpcClient({
      url: "ws://127.0.0.1:39531",
      token: TOKEN,
      logger,
      createWebSocket: (url) => {
        // The test double implements exactly the socket members IpcClient uses;
        // the cast avoids reproducing ws's unrelated 30-member public surface.
        return new FakeWebSocket(url) as unknown as WebSocket;
      },
      onFrame: () => undefined,
    });

    c.start();
    const connectedSocket = FakeWebSocket.latest;
    if (connectedSocket === undefined) {
      expect.fail("expected IpcClient to construct a WebSocket");
    }
    connectedSocket.dispatchEvent(new Event("open"));
    connectedSocket.bufferedAmount = 2 * 1024 * 1024;

    expect(c.send(playPause)).toBe(false);
    expect(c.send(playPause)).toBe(false);
    expect(connectedSocket.sent).toHaveLength(1); // hello only
    expect(connectedSocket.closes).toEqual([{ code: 1013, reason: "IPC peer backpressure" }]);
    expect(calls.warn.filter(({ obj }) => obj.evt === "ipc.backpressure")).toEqual([
      {
        obj: {
          evt: "ipc.backpressure",
          bufferedBytes: 2 * 1024 * 1024,
          frameBytes: Buffer.byteLength(JSON.stringify(playPause)),
          maxBufferedBytes: 2 * 1024 * 1024,
        },
        msg: "tray app is not reading IPC frames; closing unhealthy connection",
      },
    ]);

    connectedSocket.dispatchEvent(new Event("close"));
    c.stop();
  });

  it("returns false while the remote peer is closing an otherwise connected socket", () => {
    class FakeWebSocket extends EventTarget {
      bufferedAmount = 0;
      readyState: number = WebSocket.OPEN;
      readonly sent: string[] = [];
      readonly closes: { code?: number; reason?: string }[] = [];

      send(payload: string): void {
        this.sent.push(payload);
      }

      close(code?: number, reason?: string): void {
        this.closes.push({
          ...(code === undefined ? {} : { code }),
          ...(reason === undefined ? {} : { reason }),
        });
      }
    }
    const socket = new FakeWebSocket();
    const { logger } = makeLogger();
    const c = new IpcClient({
      url: "ws://127.0.0.1:39531",
      token: TOKEN,
      logger,
      createWebSocket: () => {
        // The test double implements exactly the socket members IpcClient uses.
        return socket as unknown as WebSocket;
      },
      onFrame: () => undefined,
    });

    c.start();
    socket.dispatchEvent(new Event("open"));
    socket.readyState = WebSocket.CLOSING;

    expect(c.state).toBe("connected"); // close handshake has not completed
    expect(socket.closes).toEqual([]);
    expect(c.send(playPause)).toBe(false);
    expect(socket.sent).toHaveLength(1); // hello only
    c.stop();
  });
});

describe("IpcClient (integration, real ws mock server)", () => {
  let waiter: Waiter;
  let server: MockTrayServer;
  let client: IpcClient | undefined;

  beforeEach(() => {
    waiter = new Waiter();
    server = new MockTrayServer(waiter);
  });

  afterEach(async () => {
    client?.stop();
    client = undefined;
    await server.close();
    vi.useRealTimers();
  });

  function createClient(
    port: number,
    backoff?: BackoffOptions,
  ): {
    client: IpcClient;
    calls: { info: LogCall[]; warn: LogCall[] };
    frames: TrayFrame[];
    states: IpcClientState[];
  } {
    const { logger, calls } = makeLogger();
    const frames: TrayFrame[] = [];
    const states: IpcClientState[] = [];
    const c = new IpcClient({
      url: `ws://127.0.0.1:${String(port)}`,
      token: TOKEN,
      logger,
      ...(backoff === undefined ? {} : { backoff }),
      onFrame: (frame) => {
        frames.push(frame);
        waiter.notify();
      },
      onStateChange: (state) => {
        states.push(state);
        waiter.notify();
      },
    });
    client = c;
    return { client: c, calls, frames, states };
  }

  /** Grabs an ephemeral port that is free but has nothing listening on it. */
  async function absentPeerPort(): Promise<number> {
    const probe = new MockTrayServer(waiter);
    const port = await probe.listen(0);
    await probe.close();
    return port;
  }

  it("sends hello first with the session token, round-trips actions, and surfaces inbound tray frames", async () => {
    const port = await server.listen();
    const { client: c, calls, frames } = createClient(port);
    c.start();

    await waiter.until(() => server.frames.length >= 1, "hello frame");
    expect(server.frames[0]).toEqual({ v: 5, type: "hello", token: TOKEN, protocol: 1 });

    await waiter.until(() => frames.length >= 1, "state-on-connect frame");
    expect(frames[0]).toEqual(STATE_ON_CONNECT);

    const action: OutboundFrame = { v: 5, type: "action", id: UUID, name: "setVolume", value: 40 };
    expect(c.send(action)).toBe(true);
    await waiter.until(() => server.frames.length >= 2, "action frame at server");
    expect(server.frames[1]).toEqual(action);

    server.sendRaw(JSON.stringify({ v: 5, type: "ack", id: UUID, ok: true }));
    await waiter.until(() => frames.length >= 2, "ack frame at client");
    expect(frames[1]).toEqual({ v: 5, type: "ack", id: UUID, ok: true });

    expect(dropWarns(calls)).toHaveLength(0);
    // Security invariant: the session token is never logged.
    expect(JSON.stringify([...calls.info, ...calls.warn])).not.toContain(TOKEN);
  });

  it.each([
    [
      "malformed JSON",
      () => {
        server.sendRaw("this is not json");
      },
      "not JSON",
    ],
    [
      "schema-invalid JSON",
      () => {
        server.sendRaw(JSON.stringify({ v: 5, type: "state", volume: 400, muted: false }));
      },
      "schema mismatch",
    ],
    [
      "binary data",
      () => {
        server.sendBinary(Buffer.from([1, 2, 3]));
      },
      "non-text message",
    ],
  ])(
    "closes with policy code 1008 on the first %s frame and reconnects",
    async (_case, send, reason) => {
      vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
      const port = await server.listen();
      const {
        client: c,
        calls,
        frames,
        states,
      } = createClient(port, {
        baseMs: 10,
        capMs: 10,
      });
      c.start();
      await waiter.until(() => frames.length >= 1, "state-on-connect frame");

      send();

      await waiter.until(() => server.closes.length >= 1, "policy close after invalid frame");
      expect(server.closes[0]).toEqual({ code: 1008, reason: "invalid inbound frame" });
      expect(frames).toHaveLength(1);
      expect(invalidFrameWarns(calls)).toEqual([
        {
          obj: { evt: "ipc.invalid-frame", reason },
          msg: "closing connection after invalid inbound frame",
        },
      ]);
      expect(versionMismatchWarns(calls)).toHaveLength(0);
      expect(c.send(playPause)).toBe(false);

      await vi.advanceTimersByTimeAsync(20);
      await waiter.until(() => server.connections >= 2, "bounded reconnect after invalid frame");
      await waiter.until(() => frames.length >= 2, "state from replacement connection");
      expect(states).toContain("waiting");
      expect(c.send(playPause)).toBe(true);
    },
  );

  it("keeps increasing backoff when each peer sends one valid frame before an invalid frame", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const port = await server.listen();
    const { client: c, frames, states } = createClient(port, { baseMs: 100, capMs: 800 });
    c.start();
    await waiter.until(() => frames.length >= 1, "initial state frame");

    server.sendRaw("not json");
    await waiter.until(() => count(states, "waiting") >= 1, "waiting after invalid frame 1");
    await vi.advanceTimersByTimeAsync(130); // attempt 0 maximum is 125 ms
    await waiter.until(() => frames.length >= 2, "replacement state frame 1");

    server.sendRaw("not json");
    await waiter.until(() => count(states, "waiting") >= 2, "waiting after invalid frame 2");
    const afterSecondReject = server.connections;
    await vi.advanceTimersByTimeAsync(130); // attempt 1 minimum is 150 ms
    await drainIo();
    expect(server.connections).toBe(afterSecondReject);
    await vi.advanceTimersByTimeAsync(130); // attempt 1 maximum is 250 ms
    await waiter.until(() => frames.length >= 3, "replacement state frame 2");

    server.sendRaw("not json");
    await waiter.until(() => count(states, "waiting") >= 3, "waiting after invalid frame 3");
    const afterThirdReject = server.connections;
    await vi.advanceTimersByTimeAsync(290); // attempt 2 minimum is 300 ms
    await drainIo();
    expect(server.connections).toBe(afterThirdReject);
    await vi.advanceTimersByTimeAsync(220); // attempt 2 maximum is 500 ms
    await waiter.until(() => frames.length >= 4, "replacement state frame 3");
  });

  it("auth reject: closes, backs off with growing delay, reconnects when accepted, and resets backoff after an established session", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const port = await server.listen();
    server.mode = "reject";
    const { client: c, frames, states } = createClient(port);
    c.start();

    await waiter.until(() => server.frames.length >= 1, "hello 1");
    await waiter.until(() => count(states, "waiting") >= 1, "waiting after reject 1");
    // Attempt 0 delay is in [375, 625] ms: nothing before the minimum...
    await vi.advanceTimersByTimeAsync(370);
    await drainIo();
    expect(server.frames).toHaveLength(1);
    // ...and a retry by the maximum.
    await vi.advanceTimersByTimeAsync(260); // t=630
    await waiter.until(() => server.frames.length >= 2, "hello 2");
    await waiter.until(() => count(states, "waiting") >= 2, "waiting after reject 2");

    // Attempt 1 delay grows to [750, 1250] ms: still rejected, still backing off.
    await vi.advanceTimersByTimeAsync(740); // t=1370 < 630+750
    await drainIo();
    expect(server.frames).toHaveLength(2);
    server.mode = "accept";
    await vi.advanceTimersByTimeAsync(520); // t=1890 >= 630+1250
    await waiter.until(() => server.frames.length >= 3, "hello 3");
    await waiter.until(() => c.state === "connected" && frames.length >= 1, "session established");

    expect(c.send(playPause)).toBe(true);
    await waiter.until(() => server.frames.length >= 4, "action after recovery");
    expect(server.frames[3]).toEqual(playPause);

    // Established session reset the attempt counter: after a drop, the next
    // retry is due within [375, 625] ms again (unreset it would be >= 1500).
    server.closeClients();
    await waiter.until(() => count(states, "waiting") >= 3, "waiting after connection drop");
    await vi.advanceTimersByTimeAsync(630);
    await waiter.until(() => server.frames.length >= 5, "fast hello after backoff reset");
    expect(server.frames[4]).toMatchObject({ type: "hello" });
  });

  it("logs repeated version-rejected handshakes once plus a periodic summary", async () => {
    const port = await server.listen();
    server.mode = "reject";
    const { client: c, calls } = createClient(port, { baseMs: 5, capMs: 5 });
    c.start();

    await waiter.until(
      () =>
        versionMismatchWarns(calls).some(({ obj }) => obj.evt === "ipc.version-mismatch.summary"),
      "version mismatch summary",
    );
    c.stop();

    expect(versionMismatchWarns(calls)).toEqual([
      {
        obj: {
          evt: "ipc.version-mismatch",
          diagnosis: "tray-sidecar-version-mismatch",
          rejectedHandshakes: 1,
          sidecarFrameVersion: 5,
          handshakeProtocol: 1,
          closeCode: 1008,
          closeReason: "auth rejected",
        },
        msg: "IPC VERSION MISMATCH: tray rejected this sidecar hello; update tray and sidecar to the same MatterHelm release",
      },
      {
        obj: {
          evt: "ipc.version-mismatch.summary",
          diagnosis: "tray-sidecar-version-mismatch",
          rejectedHandshakes: 10,
          sidecarFrameVersion: 5,
          handshakeProtocol: 1,
          closeCode: 1008,
          closeReason: "auth rejected",
        },
        msg: "IPC VERSION MISMATCH persists; tray keeps rejecting this sidecar hello; update both components to the same MatterHelm release",
      },
    ]);
    expect(calls.info.filter(({ obj }) => obj.evt === "ipc.connected")).toHaveLength(1);
    expect(calls.info.filter(({ obj }) => obj.evt === "ipc.disconnected")).toEqual([]);
  });

  it("peer absent: actions drop with exactly one WARN per outage, then resume after reconnect", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const port = await absentPeerPort();
    const { client: c, calls, states } = createClient(port);
    c.start();
    await waiter.until(() => count(states, "waiting") >= 1, "first connect failure");

    expect(c.send(playPause)).toBe(false);
    expect(c.send(playPause)).toBe(false);
    expect(c.send(playPause)).toBe(false);
    expect(dropWarns(calls)).toHaveLength(1); // one WARN for the whole outage

    await server.listen(port);
    await vi.advanceTimersByTimeAsync(700); // > max attempt-0 delay of 625 ms
    await waiter.until(() => c.state === "connected", "reconnected");

    expect(c.send(playPause)).toBe(true);
    await waiter.until(() => server.frames.length >= 2, "action after reconnect");
    expect(server.frames[0]).toMatchObject({ type: "hello" });
    expect(server.frames[1]).toEqual(playPause);

    server.closeClients();
    await waiter.until(() => count(states, "waiting") >= 2, "second outage");
    expect(c.send(playPause)).toBe(false);
    expect(dropWarns(calls)).toHaveLength(2); // new outage, one new WARN
  });

  it("stop() cancels a pending reconnect; send() after stop drops without throwing", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const port = await absentPeerPort();
    const { client: c, states } = createClient(port);
    c.start();
    await waiter.until(() => count(states, "waiting") >= 1, "connect failure");

    c.stop();
    c.stop(); // idempotent
    expect(c.state).toBe("stopped");

    await server.listen(port);
    await vi.advanceTimersByTimeAsync(80_000); // beyond any capped backoff delay
    await drainIo();
    expect(server.connections).toBe(0);
    expect(c.send(playPause)).toBe(false);
  });

  it("stop() while connected closes the socket and never reconnects", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const port = await server.listen();
    const { client: c, states } = createClient(port);
    c.start();
    await waiter.until(() => c.state === "connected", "connected");

    c.stop();
    expect(c.state).toBe("stopped");
    await vi.advanceTimersByTimeAsync(80_000);
    await drainIo();
    expect(server.connections).toBe(1); // no new connection ever appeared
    expect(states).not.toContain("waiting");
  });

  it("start() twice is an internal bug and throws", async () => {
    const port = await server.listen();
    const { client: c } = createClient(port);
    c.start();
    expect(() => {
      c.start();
    }).toThrow(/start/);
  });
});
