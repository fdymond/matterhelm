/**
 * Bridge assembly (docs/BLUEPRINT.md §2.1/§2.2 + ADR-004): ServerNode +
 * Aggregator + the config-derived endpoint set (enabled built-ins, then one
 * momentary plug per custom command), matter events translated into plain
 * `ClusterWrite` descriptors for the composition root. This module's exported
 * surface (`createBridge`, `BridgeOptions`, `BridgeHandle`) is plain data —
 * matter.js stays behind ./adapter.js, the only module importing `@matter/*`.
 *
 * Trigger source (ADR-008): plug endpoints — the momentary transport buttons,
 * every custom command, and the stateful power switch — dispatch from the
 * OnOff **command** matter.js received, not from the attribute change it
 * produced. Matter's `onOff` attribute is read-only, so every controller
 * change arrives as a command, while matter.js emits no change event for a
 * command that writes the value already held: observing changes silently
 * dropped repeated identical commands ("turn on HTPC Next" twice inside the
 * reset window, "turn off HTPC Power" when it already reads off). The Speaker
 * endpoint keeps attribute observation — its state is written locally by the
 * tray app and read back, which is what echo suppression below exists for.
 *
 * Echo suppression (local-write loop, Speaker only): matter.js fires `$Changed` for local
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
 * tray app already has). Momentary resets need no suppression at all: the
 * reset is a direct attribute write, invokes no command, and so never reaches
 * the command observer (ADR-008) — which is what makes it safe for
 * `mapping/actions.ts` to treat EVERY controller command on a momentary
 * endpoint, Off included, as a press (S8-4).
 *
 * Momentary auto-reset (§2.2, custom plugs included per ADR-004): a
 * momentary endpoint's On command schedules a write of `off`
 * {@link BridgeOptions.momentaryResetMs} (default
 * {@link DEFAULT_MOMENTARY_RESET_MS}) later, so voice, app taps, and routines
 * present as one button press in the Home app. Since ADR-008 this window is
 * **presentation only** — dispatch no longer depends on the attribute having
 * been returned to `off` first. A second On command before the reset restarts
 * the window (last press wins) and dispatches again; an Off command from the
 * controller cancels it. Timers are keyed by Matter endpoint id and cleared
 * on {@link BridgeHandle.close}.
 */
import type { ClusterWrite } from "../mapping/actions.js";

import { MatterNode } from "./adapter.js";
import type { PairingCodes, PlugHandle, SpeakerHandle } from "./adapter.js";
import { bridgeIdentity, endpointSpecs } from "./devices.js";
import type { BridgedDeviceKind, BuiltinEndpointKey, EndpointsConfig } from "./devices.js";
import type { DiagnosticsLogger, MatterLogLevel } from "./diagnostics.js";

export type { PairingCodes } from "./adapter.js";
export type { BuiltinEndpointKey, EndpointsConfig, MomentaryEndpointKey } from "./devices.js";
export type { DiagnosticsLogger, MatterLogLevel } from "./diagnostics.js";

/**
 * §2.2 as amended by S7-1/S8-2: default ms after an On command that momentary
 * endpoints auto-reset to `off` — 0 = the next tick after the command commits
 * (safe post-ADR-008: the window is presentation only). Config-driven via
 * `HTPC_BRIDGE_MOMENTARY_RESET_MS` (0–2000); this default must equal the tray
 * app's `momentaryResetMs` default — the two sides ship as one product.
 */
export const DEFAULT_MOMENTARY_RESET_MS = 0;

/** Sanctioned Matter test VID/PID defaults (ADR-002); both configurable. */
export const DEFAULT_VENDOR_ID = 0xfff1;
export const DEFAULT_PRODUCT_ID = 0x8000;

/**
 * Default identity seed. Stable across restarts by construction — a changed
 * seed changes every uniqueId and Google Home re-adds all devices.
 */
const DEFAULT_UNIQUE_ID_SEED = "htpc-matter-bridge";

// S7-2 rename note: NODE_ID and the uniqueId seed are commissioning-critical
// identity (changing either re-adds every device in Google Home), and the
// vendor/product names are the paired bridge's user-facing Basic Information
// strings — all three deliberately keep their pre-MatterHelm values. Only
// packaging/docs carry the new product name.
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
  /** Endpoint set + display names (ADR-004 §2; `config.ts` parses this). */
  endpoints: EndpointsConfig;
  /**
   * Momentary auto-reset window in ms (S7-1); unset =
   * {@link DEFAULT_MOMENTARY_RESET_MS}. `config.ts` validates the 0–2000
   * range (0 = next-tick reset, S8-2) — this module trusts its caller.
   */
  momentaryResetMs?: number;
  /** Pins the mDNS interface for multi-NIC hosts; see `./adapter.js`. */
  mdnsInterface?: string;
  /**
   * Diagnostics seam (ADR-006 §1; see `MatterNodeOptions.logger`): matter.js
   * logs become `{evt:"matter.log"}` pino events and session lifecycle is
   * logged. Unset (tests, smoke script) = matter.js console logging.
   */
  logger?: DiagnosticsLogger;
  /** matter.js global log level; unset = matter.js's default. */
  matterLogLevel?: MatterLogLevel;
  /** Per-facility matter.js level overrides; unset = none. */
  matterLogFacilities?: Readonly<Record<string, MatterLogLevel>>;
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

/** One constructed endpoint, as plain data (startup logging/diagnostics). */
export interface ConstructedEndpoint {
  /** Matter endpoint id: a built-in role or `custom-<key>`. */
  id: string;
  /** Display name — the Google voice target. */
  name: string;
  kind: BridgedDeviceKind;
}

export interface BridgeHandle {
  /** Brings the node online (network + mDNS). Call once. */
  start(): Promise<void>;
  /** Cancels reset timers and shuts the node down. Idempotent, terminal. */
  close(): Promise<void>;
  /** The endpoint set actually constructed, in add (endpoint-number) order. */
  readonly endpoints: readonly ConstructedEndpoint[];
  /** Null until started, and null once commissioned (spike semantics). */
  readonly pairingCodes: PairingCodes | null;
  readonly isCommissioned: boolean;
  /** `cb(true)` on commissioning, `cb(false)` when the controller leaves. */
  onCommissionedChange(cb: (commissioned: boolean) => void): void;
  /**
   * Applies tray-app state (`mapping/state.ts` output) to the Speaker
   * endpoint: LevelControl currentLevel 0-254 + OnOff (true = unmuted).
   * Echo-suppressed — does not re-emit `onClusterWrite`. A no-op when the
   * speaker endpoint is disabled (ADR-004: state frames stay tolerated).
   */
  setSpeakerState(level0to254: number, onOff: boolean): Promise<void>;
  /**
   * Writes a momentary endpoint's OnOff back to `false` (§2.2 reset).
   * `endpointId` is the Matter endpoint id (e.g. `playpause`,
   * `custom-movie-mode`); an id that names no momentary endpoint throws —
   * that is an internal bug, not an input.
   */
  resetMomentary(endpointId: string): Promise<void>;
  /**
   * Invokes a plug endpoint's OnOff **command** locally, exactly as a
   * controller would (ADR-008). Product code never calls this — it exists so
   * `smoke.ts` can prove command interception against a live node without a
   * Matter controller. An id that names no plug throws.
   */
  invokePlugOnOff(endpointId: string, on: boolean): Promise<void>;
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
 * Per-endpoint reset timers for the momentary plugs (built-in and custom),
 * keyed by Matter endpoint id. Pure scheduling — the actual `off` write
 * happens in the injected `onReset` callback.
 */
export class MomentaryResetScheduler<K extends string = string> {
  readonly #delayMs: number;
  readonly #onReset: (endpoint: K) => void;
  readonly #timers = new Map<K, NodeJS.Timeout>();

  constructor(delayMs: number, onReset: (endpoint: K) => void) {
    this.#delayMs = delayMs;
    this.#onReset = onReset;
  }

  /** An `on` write: (re)starts the endpoint's reset window (last tap wins). */
  noteOn(endpoint: K): void {
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
  noteOff(endpoint: K): void {
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

/** The built-in roles carried by an On/Off Plug-in Unit endpoint. */
export type PlugEndpointKey = Exclude<BuiltinEndpointKey, "speaker">;

/**
 * Something observed on one of our endpoints, as plain data. The `kind`
 * discriminant also names the observation channel (ADR-008): the speaker is
 * watched by attribute subscription, plugs by OnOff command invocation.
 */
export type EndpointEvent =
  /** Speaker OnOff attribute change (mute); echo-suppressed for local writes. */
  | { kind: "speakerOnOff"; on: boolean }
  /** Speaker LevelControl change; `null` is matter.js's "no level set". */
  | { kind: "speakerLevel"; level: number | null }
  /** A built-in plug's OnOff command — `on` is the command, not a new state. */
  | { kind: "plugCommand"; key: PlugEndpointKey; on: boolean }
  /** A custom command plug's OnOff command (ADR-004). */
  | { kind: "customCommand"; customKey: string; on: boolean };

/**
 * Translates an observed endpoint event into the `ClusterWrite` descriptor
 * `mapping/actions.ts` consumes, or `null` for a LevelControl `null` level
 * (matter.js's "no level set" — nothing to tell the tray app).
 *
 * `ClusterWrite` keeps its pre-ADR-008 shape: what changed for plugs is where
 * the observation comes from, not what the rest of the pipeline (and the IPC
 * protocol behind it) sees.
 */
export function endpointEventToClusterWrite(event: EndpointEvent): ClusterWrite | null {
  switch (event.kind) {
    case "speakerOnOff": {
      return { endpoint: "speaker", cluster: "onOff", on: event.on };
    }
    case "speakerLevel": {
      if (event.level === null) {
        return null;
      }
      return { endpoint: "speaker", cluster: "levelControl", level: event.level };
    }
    case "plugCommand": {
      // Split so each literal endpoint name matches its own ClusterWrite member.
      return event.key === "power"
        ? { endpoint: "power", cluster: "onOff", on: event.on }
        : { endpoint: event.key, cluster: "onOff", on: event.on };
    }
    case "customCommand": {
      return { endpoint: "custom", key: event.customKey, cluster: "onOff", on: event.on };
    }
  }
}

/**
 * Builds one plug endpoint's OnOff command handler: emit the event the
 * command means, and — for a momentary endpoint — (re)arm or cancel its
 * auto-reset window. `reset` is omitted for the stateful power plug, which
 * has no window.
 *
 * Every invocation emits, including a repeat of the command the endpoint just
 * received: that repetition is exactly what the pre-ADR-008 attribute-change
 * wiring could not see.
 */
export function makePlugCommandHandler(
  event: (on: boolean) => EndpointEvent,
  emit: (event: EndpointEvent) => void,
  reset?: { noteOn: () => void; noteOff: () => void },
): (on: boolean) => void {
  return (on) => {
    if (reset !== undefined) {
      if (on) {
        reset.noteOn();
      } else {
        reset.noteOff();
      }
    }
    emit(event(on));
  };
}

/**
 * Builds the whole bridge: node + aggregator + the config-derived endpoint
 * set (§2.2 order, then custom commands), wired per the module doc. The
 * returned handle is the matter/ package's entire outward API.
 */
export async function createBridge(options: BridgeOptions): Promise<BridgeHandle> {
  const seed = options.uniqueIdSeed ?? DEFAULT_UNIQUE_ID_SEED;
  const identity = bridgeIdentity(seed);
  const node = await MatterNode.create({
    id: NODE_ID,
    storageDir: options.storageDir,
    ...(options.port === undefined ? {} : { port: options.port }),
    ...(options.mdnsInterface === undefined ? {} : { mdnsInterface: options.mdnsInterface }),
    ...(options.logger === undefined ? {} : { logger: options.logger }),
    ...(options.matterLogLevel === undefined ? {} : { matterLogLevel: options.matterLogLevel }),
    ...(options.matterLogFacilities === undefined
      ? {}
      : { matterLogFacilities: options.matterLogFacilities }),
    vendorId: options.vendorId ?? DEFAULT_VENDOR_ID,
    productId: options.productId ?? DEFAULT_PRODUCT_ID,
    vendorName: VENDOR_NAME,
    productName: PRODUCT_NAME,
    serialNumber: identity.serialNumber,
    uniqueId: identity.uniqueId,
  });

  const suppressor = new EchoSuppressor();

  const emit = (event: EndpointEvent): void => {
    const write = endpointEventToClusterWrite(event);
    if (write !== null) {
      options.onClusterWrite(write);
    }
  };

  /** Every plug endpoint by Matter endpoint id. */
  const plugs = new Map<string, PlugHandle>();
  /** The subset that auto-resets — the reset targets. */
  const momentaryIds = new Set<string>();

  const resetMomentary = async (endpointId: string): Promise<void> => {
    const plug = momentaryIds.has(endpointId) ? plugs.get(endpointId) : undefined;
    if (plug === undefined) {
      // Fail loud: only this module schedules resets, so an unknown id is a bug.
      throw new Error(
        `resetMomentary: no momentary endpoint with id ${JSON.stringify(endpointId)}`,
      );
    }
    await plug.setOnOff(false);
  };

  const scheduler = new MomentaryResetScheduler<string>(
    options.momentaryResetMs ?? DEFAULT_MOMENTARY_RESET_MS,
    (endpointId) => {
      // A failed write to our own endpoint is an internal invariant violation;
      // the floating promise surfaces it as an unhandled rejection (fail loud,
      // docs/ENGINEERING-STANDARDS.md). close() clears timers first, so this
      // cannot fire against a closed node.
      void resetMomentary(endpointId);
    },
  );

  // Config-derived endpoint set: enabled built-ins in §2.2 order, then one
  // momentary plug per custom command (endpoint numbers follow add order).
  const specs = endpointSpecs(options.endpoints, seed);
  const constructed: ConstructedEndpoint[] = [];
  let speaker: SpeakerHandle | undefined;

  /** The momentary endpoints' half of the command handler: their reset window. */
  const resetWindowFor = (id: string): { noteOn: () => void; noteOff: () => void } => ({
    noteOn: () => {
      scheduler.noteOn(id);
    },
    noteOff: () => {
      scheduler.noteOff(id);
    },
  });

  for (const spec of specs) {
    if (spec.role === "speaker") {
      const speakerHandle = await node.addSpeaker(spec.info);
      speaker = speakerHandle;
      speakerHandle.onOnOffChanged((on) => {
        if (suppressor.check(SPEAKER_ONOFF, on)) {
          return;
        }
        emit({ kind: "speakerOnOff", on });
      });
      speakerHandle.onLevelChanged((level) => {
        if (level !== null && suppressor.check(SPEAKER_LEVEL, level)) {
          return;
        }
        emit({ kind: "speakerLevel", level });
      });
    } else {
      const id = spec.info.id;
      switch (spec.role) {
        case "custom": {
          const customKey = spec.key;
          plugs.set(
            id,
            await node.addPlug(
              spec.info,
              makePlugCommandHandler(
                (on) => ({ kind: "customCommand", customKey, on }),
                emit,
                resetWindowFor(id),
              ),
            ),
          );
          momentaryIds.add(id);
          break;
        }
        case "playPause":
        case "next":
        case "previous": {
          const key = spec.role;
          plugs.set(
            id,
            await node.addPlug(
              spec.info,
              makePlugCommandHandler(
                (on) => ({ kind: "plugCommand", key, on }),
                emit,
                resetWindowFor(id),
              ),
            ),
          );
          momentaryIds.add(id);
          break;
        }
        case "power": {
          // The stateful power toggle: no reset window, every command dispatches.
          plugs.set(
            id,
            await node.addPlug(
              spec.info,
              makePlugCommandHandler((on) => ({ kind: "plugCommand", key: "power", on }), emit),
            ),
          );
          break;
        }
      }
    }
    constructed.push({ id: spec.info.id, name: spec.info.name, kind: spec.kind });
  }

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
    endpoints: constructed,
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
      if (speaker === undefined) {
        return; // speaker disabled (ADR-004): tolerate state, apply nothing
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
    invokePlugOnOff: async (endpointId, on): Promise<void> => {
      const plug = plugs.get(endpointId);
      if (plug === undefined) {
        throw new Error(`invokePlugOnOff: no plug endpoint with id ${JSON.stringify(endpointId)}`);
      }
      await plug.invokeOnOff(on);
    },
  };
}
