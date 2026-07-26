/**
 * Bridge assembly (docs/BLUEPRINT.md §2.1/§2.2): ServerNode + Aggregator +
 * the five endpoints, matter events translated into plain `ClusterWrite`
 * descriptors for the composition root. This module's exported surface
 * (`createBridge`, `BridgeOptions`, `BridgeHandle`) is plain data — matter.js
 * stays behind ./adapter.js, the only module importing `@matter/*`.
 *
 * Echo suppression (local-write loop): matter.js fires `$Changed` for local
 * attribute writes exactly like remote ones, so applying tray-app state via
 * {@link BridgeHandle.setSpeakerState} would echo straight back out as a
 * `ClusterWrite`. Suppression is value-based and FIFO per attribute
 * ({@link EchoSuppressor}): before each local speaker write the new value is
 * enqueued as "expected"; a change event matching the queue head is consumed
 * silently. Values are only enqueued when they differ from the current
 * attribute value (matter.js emits no event for a no-op write, so an
 * unconditional enqueue would leak stale expectations that could later
 * swallow a genuine remote write). Concurrency caveat: a remote write that
 * commits between our read and our write can pair with the wrong queue entry
 * when it carries the identical value — the suppressed event is then the
 * remote one, which is harmless (the state it reports is exactly what the
 * tray app already has). Momentary resets are deliberately NOT suppressed:
 * their `false` change event flows to `onClusterWrite`, and
 * `mapping/actions.ts` maps it to `null` (no action) by design.
 *
 * Momentary auto-reset (§2.2): a momentary endpoint's `on` write schedules a
 * write of `off` {@link MOMENTARY_RESET_MS} later, so voice, app taps, and
 * routines behave as one button press. A second `on` before the reset
 * restarts the window (last tap wins — matter.js emits no event for a
 * value-unchanged write, so consecutive `on` events imply an interleaved
 * `off`); an `off` from the controller cancels it. Timers are per endpoint
 * and cleared on {@link BridgeHandle.close}.
 */
import type { ClusterWrite } from "../mapping/actions.js";

import { MatterNode } from "./adapter.js";
import type { PairingCodes, PlugHandle, SpeakerHandle } from "./adapter.js";
import { MOMENTARY_ENDPOINT_KEYS, bridgeIdentity, endpointSpecs } from "./devices.js";
import type { DeviceNames, EndpointKey, MomentaryEndpointKey } from "./devices.js";

export type { PairingCodes } from "./adapter.js";
export type { DeviceNames, EndpointKey, MomentaryEndpointKey } from "./devices.js";

/** §2.2: momentary endpoints auto-reset to `off` this long after `on`. */
export const MOMENTARY_RESET_MS = 800;

/** Sanctioned Matter test VID/PID defaults (ADR-002); both configurable. */
export const DEFAULT_VENDOR_ID = 0xfff1;
export const DEFAULT_PRODUCT_ID = 0x8000;

/**
 * Default identity seed. Stable across restarts by construction — a changed
 * seed changes every uniqueId and Google Home re-adds all devices.
 */
const DEFAULT_UNIQUE_ID_SEED = "htpc-matter-bridge";

const NODE_ID = "htpc-bridge";
const VENDOR_NAME = "HTPC Bridge";
const PRODUCT_NAME = "HTPC Matter Bridge";

/** Suppression keys — one FIFO queue per locally-written attribute. */
const SPEAKER_ONOFF = "speaker.onOff";
const SPEAKER_LEVEL = "speaker.level";

export interface BridgeOptions {
  /** matter.js storage dir (fabric credentials; delete = factory reset). */
  storageDir: string;
  /** Matter UDP/TCP port; matter.js defaults to 5540 when omitted. */
  port?: number;
  /** Display names — the Google voice targets (BLUEPRINT §2.2). */
  deviceNames: DeviceNames;
  /** Pins the mDNS interface for multi-NIC hosts; see `./adapter.js`. */
  mdnsInterface?: string;
  /** Defaults to {@link DEFAULT_VENDOR_ID} (test VID, ADR-002). */
  vendorId?: number;
  /** Defaults to {@link DEFAULT_PRODUCT_ID} (test PID, ADR-002). */
  productId?: number;
  /** Endpoint-identity seed; keep stable across restarts (see devices.ts). */
  uniqueIdSeed?: string;
  /**
   * Called for every externally-caused cluster write (never for the local
   * writes this module itself applies — see module doc on echo suppression).
   * The composition root feeds these through `mapping/actions.ts`.
   */
  onClusterWrite: (write: ClusterWrite) => void;
}

export interface BridgeHandle {
  /** Brings the node online (network + mDNS). Call once. */
  start(): Promise<void>;
  /** Cancels reset timers and shuts the node down. Idempotent, terminal. */
  close(): Promise<void>;
  /** Null until started, and null once commissioned (spike semantics). */
  readonly pairingCodes: PairingCodes | null;
  readonly isCommissioned: boolean;
  /** `cb(true)` on commissioning, `cb(false)` when the controller leaves. */
  onCommissionedChange(cb: (commissioned: boolean) => void): void;
  /**
   * Applies tray-app state (`mapping/state.ts` output) to the Speaker
   * endpoint: LevelControl currentLevel 0-254 + OnOff (true = unmuted).
   * Echo-suppressed — does not re-emit `onClusterWrite`.
   */
  setSpeakerState(level0to254: number, onOff: boolean): Promise<void>;
  /** Writes a momentary endpoint's OnOff back to `false` (§2.2 reset). */
  resetMomentary(endpoint: MomentaryEndpointKey): Promise<void>;
}

/**
 * Value-based FIFO echo suppression for local attribute writes. See the
 * module doc for the design and its concurrency caveat.
 */
export class EchoSuppressor {
  readonly #pending = new Map<string, unknown[]>();

  /** Records that a local write of `value` on `key` is about to commit. */
  expect(key: string, value: unknown): void {
    const queue = this.#pending.get(key);
    if (queue === undefined) {
      this.#pending.set(key, [value]);
    } else {
      queue.push(value);
    }
  }

  /**
   * True when `value` matches the oldest outstanding local write on `key`
   * (consuming it) — the caller must then suppress the change event. A
   * non-matching value leaves the queue untouched: matter.js delivers change
   * events in commit order, so our own write's event is still en route.
   */
  check(key: string, value: unknown): boolean {
    const queue = this.#pending.get(key);
    if (queue === undefined || queue.length === 0) {
      return false;
    }
    if (Object.is(queue[0], value)) {
      queue.shift();
      return true;
    }
    return false;
  }
}

/**
 * Per-endpoint reset timers for the momentary switches. Pure scheduling —
 * the actual `off` write happens in the injected `onReset` callback.
 */
export class MomentaryResetScheduler {
  readonly #delayMs: number;
  readonly #onReset: (endpoint: MomentaryEndpointKey) => void;
  readonly #timers = new Map<MomentaryEndpointKey, NodeJS.Timeout>();

  constructor(delayMs: number, onReset: (endpoint: MomentaryEndpointKey) => void) {
    this.#delayMs = delayMs;
    this.#onReset = onReset;
  }

  /** An `on` write: (re)starts the endpoint's reset window (last tap wins). */
  noteOn(endpoint: MomentaryEndpointKey): void {
    this.noteOff(endpoint);
    this.#timers.set(
      endpoint,
      setTimeout(() => {
        this.#timers.delete(endpoint);
        this.#onReset(endpoint);
      }, this.#delayMs),
    );
  }

  /** An `off` write (controller or our own reset): cancels a pending reset. */
  noteOff(endpoint: MomentaryEndpointKey): void {
    const timer = this.#timers.get(endpoint);
    if (timer !== undefined) {
      clearTimeout(timer);
      this.#timers.delete(endpoint);
    }
  }

  /** Cancels every pending reset (bridge close). */
  clear(): void {
    for (const timer of this.#timers.values()) {
      clearTimeout(timer);
    }
    this.#timers.clear();
  }
}

/** A change event observed on one of our endpoints, as plain data. */
export type EndpointEvent =
  | { key: EndpointKey; attribute: "onOff"; on: boolean }
  | { key: "speaker"; attribute: "level"; level: number | null };

/**
 * Translates an observed endpoint event into the `ClusterWrite` descriptor
 * `mapping/actions.ts` consumes, or `null` for a LevelControl `null` level
 * (matter.js's "no level set" — nothing to tell the tray app).
 */
export function endpointEventToClusterWrite(event: EndpointEvent): ClusterWrite | null {
  if (event.attribute === "level") {
    if (event.level === null) {
      return null;
    }
    return { endpoint: "speaker", cluster: "levelControl", level: event.level };
  }
  switch (event.key) {
    case "speaker": {
      return { endpoint: "speaker", cluster: "onOff", on: event.on };
    }
    case "power": {
      return { endpoint: "power", cluster: "onOff", on: event.on };
    }
    case "playPause":
    case "next":
    case "previous": {
      return { endpoint: event.key, cluster: "onOff", on: event.on };
    }
  }
}

/**
 * Builds the whole §2.2 bridge: node + aggregator + Speaker + three
 * momentary switches + power toggle, wired per the module doc. The returned
 * handle is the matter/ package's entire outward API.
 */
export async function createBridge(options: BridgeOptions): Promise<BridgeHandle> {
  const seed = options.uniqueIdSeed ?? DEFAULT_UNIQUE_ID_SEED;
  const identity = bridgeIdentity(seed);
  const node = await MatterNode.create({
    id: NODE_ID,
    storageDir: options.storageDir,
    ...(options.port === undefined ? {} : { port: options.port }),
    ...(options.mdnsInterface === undefined ? {} : { mdnsInterface: options.mdnsInterface }),
    vendorId: options.vendorId ?? DEFAULT_VENDOR_ID,
    productId: options.productId ?? DEFAULT_PRODUCT_ID,
    vendorName: VENDOR_NAME,
    productName: PRODUCT_NAME,
    serialNumber: identity.serialNumber,
    uniqueId: identity.uniqueId,
  });

  // §2.2 table order — endpoint numbers are assigned in add order.
  const specs = endpointSpecs(options.deviceNames, seed);
  const speaker: SpeakerHandle = await node.addSpeaker(specs.speaker.info);
  const momentary: Record<MomentaryEndpointKey, PlugHandle> = {
    playPause: await node.addPlug(specs.playPause.info),
    next: await node.addPlug(specs.next.info),
    previous: await node.addPlug(specs.previous.info),
  };
  const power: PlugHandle = await node.addPlug(specs.power.info);

  const suppressor = new EchoSuppressor();

  const emit = (event: EndpointEvent): void => {
    const write = endpointEventToClusterWrite(event);
    if (write !== null) {
      options.onClusterWrite(write);
    }
  };

  const resetMomentary = async (endpoint: MomentaryEndpointKey): Promise<void> => {
    await momentary[endpoint].setOnOff(false);
  };

  const scheduler = new MomentaryResetScheduler(MOMENTARY_RESET_MS, (endpoint) => {
    // A failed write to our own endpoint is an internal invariant violation;
    // the floating promise surfaces it as an unhandled rejection (fail loud,
    // docs/ENGINEERING-STANDARDS.md). close() clears timers first, so this
    // cannot fire against a closed node.
    void resetMomentary(endpoint);
  });

  speaker.onOnOffChanged((on) => {
    if (suppressor.check(SPEAKER_ONOFF, on)) {
      return;
    }
    emit({ key: "speaker", attribute: "onOff", on });
  });
  speaker.onLevelChanged((level) => {
    if (level !== null && suppressor.check(SPEAKER_LEVEL, level)) {
      return;
    }
    emit({ key: "speaker", attribute: "level", level });
  });
  for (const key of MOMENTARY_ENDPOINT_KEYS) {
    momentary[key].onOnOffChanged((on) => {
      if (on) {
        scheduler.noteOn(key);
      } else {
        scheduler.noteOff(key);
      }
      emit({ key, attribute: "onOff", on });
    });
  }
  power.onOnOffChanged((on) => {
    emit({ key: "power", attribute: "onOff", on });
  });

  let closed = false;

  return {
    start: () => node.start(),
    close: async (): Promise<void> => {
      if (closed) {
        return;
      }
      closed = true;
      scheduler.clear();
      await node.close();
    },
    get pairingCodes(): PairingCodes | null {
      return node.pairingCodes;
    },
    get isCommissioned(): boolean {
      return node.isCommissioned;
    },
    onCommissionedChange: (cb) => {
      node.onCommissionedChange(cb);
    },
    setSpeakerState: async (level0to254, onOff): Promise<void> => {
      if (!Number.isInteger(level0to254) || level0to254 < 0 || level0to254 > 254) {
        // Fail loud: mapping/state.ts guarantees 0-254, so this is a bug.
        throw new RangeError(
          `setSpeakerState: level must be an integer 0-254, got ${String(level0to254)}`,
        );
      }
      const patch: { level?: number; onOff?: boolean } = {};
      if (speaker.getLevel() !== level0to254) {
        patch.level = level0to254;
        suppressor.expect(SPEAKER_LEVEL, level0to254);
      }
      if (speaker.getOnOff() !== onOff) {
        patch.onOff = onOff;
        suppressor.expect(SPEAKER_ONOFF, onOff);
      }
      if (patch.level === undefined && patch.onOff === undefined) {
        return;
      }
      await speaker.setState(patch);
    },
    resetMomentary,
  };
}
