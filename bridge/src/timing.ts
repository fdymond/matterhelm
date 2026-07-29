/**
 * Per-action timing instrumentation (ADR-006 §1): `{evt:"action.timing"}`
 * for cluster-write -> WS-send elapsed and `{evt:"ack.timing"}` for
 * send -> ack latency, both keyed by the action frame's existing uuid `id`
 * (the cross-process correlation id — no protocol change).
 *
 * Everything here is dependency-injected (sender, logger, clock, id source),
 * so the exact code the composition root wires is fully unit-testable — the
 * only piece a test substitutes is the origin of the `ClusterWrite`, which
 * in production comes from a Matter controller.
 */
import type { ActionFrame } from "./ipc/protocol.js";
import { clusterWriteToAction } from "./mapping/actions.js";
import type { ClusterWrite } from "./mapping/actions.js";

/** Pending entries older than this are dropped — a tray app that never acks
 * (or an ack lost to a reconnect) must not leak map entries. */
export const PENDING_ACK_TTL_MS = 10_000;

/** Hard entry cap, evicting oldest-first. 256 in-flight unacked actions is
 * far beyond anything the momentary/speaker endpoints can produce; the cap
 * only matters if the tray app stops acking entirely, and then the TTL is
 * already discarding entries. */
export const PENDING_ACK_MAX_ENTRIES = 256;

/**
 * Rounds an elapsed-milliseconds value to microsecond precision (three
 * decimal places) — `performance.now()` deltas carry sub-µs noise that would
 * only bloat log lines.
 */
export function roundToMicros(elapsedMs: number): number {
  return Math.round(elapsedMs * 1000) / 1000;
}

/**
 * Bounded map of in-flight action sends awaiting their ack. `note(id)`
 * records the send instant; `settle(id)` returns the µs-rounded elapsed ms
 * (consuming the entry) or `null` for an unknown/expired/duplicate id.
 *
 * Eviction is sweep-on-access (both entry points), not timer-based: entries
 * older than the TTL are discarded before any other work, and a hard entry
 * cap evicts oldest-first. Map iteration order is insertion order and
 * `note` timestamps are monotonic, so sweeping from the front stops at the
 * first fresh entry. No timers means nothing to cancel on shutdown.
 */
export class PendingAckTimings {
  readonly #ttlMs: number;
  readonly #maxEntries: number;
  readonly #now: () => number;
  readonly #pending = new Map<string, number>();

  constructor(
    ttlMs: number = PENDING_ACK_TTL_MS,
    maxEntries: number = PENDING_ACK_MAX_ENTRIES,
    now: () => number = () => performance.now(),
  ) {
    this.#ttlMs = ttlMs;
    this.#maxEntries = maxEntries;
    this.#now = now;
  }

  /** Number of tracked in-flight sends (diagnostics/tests). */
  get size(): number {
    return this.#pending.size;
  }

  /** Records that action `id` was sent now. */
  note(id: string): void {
    const now = this.#now();
    this.#evictExpired(now);
    if (this.#pending.size >= this.#maxEntries) {
      // Oldest-first: the first key in insertion order is the oldest send.
      const oldest = this.#pending.keys().next();
      if (!oldest.done) {
        this.#pending.delete(oldest.value);
      }
    }
    this.#pending.set(id, now);
  }

  /**
   * Consumes the pending entry for `id`, returning the µs-rounded elapsed
   * milliseconds since its `note`, or `null` when the id is unknown, already
   * settled, or older than the TTL.
   */
  settle(id: string): number | null {
    const now = this.#now();
    this.#evictExpired(now);
    const sentAt = this.#pending.get(id);
    if (sentAt === undefined) {
      return null;
    }
    this.#pending.delete(id);
    return roundToMicros(now - sentAt);
  }

  #evictExpired(now: number): void {
    for (const [id, sentAt] of this.#pending) {
      if (now - sentAt <= this.#ttlMs) {
        break; // insertion order = time order: the rest are fresher
      }
      this.#pending.delete(id);
    }
  }
}

/** Narrow logger surface for the timing events (info-level only). */
export interface TimingLogger {
  info(obj: Record<string, unknown>, msg: string): void;
}

export interface ActionDispatcherOptions {
  /** `IpcClient.send` (or a test double): true = written to the socket. */
  send(frame: ActionFrame): boolean;
  logger: TimingLogger;
  /** Shared with {@link makeAckTimingObserver} — the send->ack ledger. */
  timings: PendingAckTimings;
  /** Action frame id source (`randomUUID` in production). */
  newId(): string;
  /** Clock override for tests; production uses `performance.now`. */
  now?: () => number;
}

/**
 * Builds the `onClusterWrite` callback the composition root hands to the
 * matter bridge: cluster write -> action frame -> send, logging
 * `{evt:"action.timing", id, name, elapsedMs}` (µs precision) right after
 * the send returns. Sends that reached the socket are noted in the pending
 * map so the matching ack's latency can be measured; dropped frames (tray
 * app away) are not — no ack can ever arrive for them.
 */
export function makeActionDispatcher(
  options: ActionDispatcherOptions,
): (write: ClusterWrite) => void {
  const now = options.now ?? ((): number => performance.now());
  return (write) => {
    const startedAt = now();
    const action = clusterWriteToAction(write, options.newId());
    if (action === null) {
      return; // momentary auto-reset echo — not a user action (§2.2)
    }
    const sent = options.send(action);
    options.logger.info(
      {
        evt: "action.timing",
        id: action.id,
        name: action.name,
        elapsedMs: roundToMicros(now() - startedAt),
      },
      "cluster write dispatched as action frame",
    );
    if (sent) {
      options.timings.note(action.id);
    }
  };
}

/**
 * Builds the ack-side observer: called with every inbound ack frame, it logs
 * `{evt:"ack.timing", id, ok, elapsedMs}` for acks whose send is still in
 * the pending map, and stays silent for unknown/expired ids (a late ack
 * after the 10 s TTL, or an ack for a frame sent before a restart).
 */
export function makeAckTimingObserver(options: {
  logger: TimingLogger;
  timings: PendingAckTimings;
}): (ack: { id: string; ok: boolean }) => void {
  return (ack) => {
    const elapsedMs = options.timings.settle(ack.id);
    if (elapsedMs !== null) {
      options.logger.info(
        { evt: "ack.timing", id: ack.id, ok: ack.ok, elapsedMs },
        "action acknowledged",
      );
    }
  };
}
