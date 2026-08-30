/**
 * Bridge assembly (docs/BLUEPRINT.md §2.1/§2.2 + ADR-004): ServerNode +
 * Aggregator + the config-derived endpoint set (enabled built-ins, then one
 * plug per custom command), matter events translated into plain
 * `ClusterWrite` descriptors for the composition root. This module's exported
 * surface (`createBridge`, `BridgeOptions`, `BridgeHandle`) is plain data —
 * matter.js stays behind ./adapter.js, the only module importing `@matter/*`.
 *
 * Trigger source (ADR-008): transport, custom, and power plug endpoints
 * dispatch from the
 * OnOff **command** matter.js received, not from the attribute change it
 * produced. Matter's `onOff` attribute is read-only, so every controller
 * change arrives as a command, while matter.js emits no change event for a
 * command that writes the value already held: observing changes silently
 * dropped repeated identical commands ("turn on HTPC Next" twice, or "turn
 * off HTPC Power" when it already reads off). The Speaker
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
 * swallow a genuine remote write). Local writes are serialized so each read
 * observes the preceding write's committed state; racing same-value tray
 * updates therefore enqueue only one expectation. A remote write that commits
 * between our read and our write can still pair with the identical expectation,
 * which is harmless because it reports exactly the state the tray already has.
 * Opted-in custom and irreversible-Power resets need no suppression: they are
 * direct attribute writes, invoke no command, and never reach the observer.
 *
 * Opt-in custom auto-reset (ADR-012): a reset-enabled custom endpoint's On
 * command schedules a write of `off`
 * {@link BridgeOptions.momentaryResetMs} (default
 * {@link DEFAULT_MOMENTARY_RESET_MS}) later, so voice, app taps, and routines
 * present as a button in the Home app. A second On command before the reset
 * restarts the window (last press wins); controller Off cancels it. Retained
 * built-ins and default custom commands never schedule a reset. Irreversible Power
 * Off uses a separate fixed next-tick scheduler to write On before the machine
 * can suspend; the custom reset delay never affects it. Timers are keyed by
 * Matter endpoint id and cleared on {@link BridgeHandle.close}. Resets whose
 * timers already fired are tracked separately: failures are logged with the
 * endpoint/policy, and close waits for those writes before closing matter.js.
 */
import type { ClusterWrite } from "../mapping/actions.js";

import { MatterNode } from "./adapter.js";
import type { PairingCodes, PlugHandle, SpeakerHandle } from "./adapter.js";
import { bridgeIdentity, endpointSpecs } from "./devices.js";
import type { BridgedDeviceKind, BuiltinEndpointKey, EndpointsConfig } from "./devices.js";
import type { DiagnosticsLogger, MatterLogLevel } from "./diagnostics.js";

export type { PairingCodes } from "./adapter.js";
export type { BuiltinEndpointKey, EndpointsConfig } from "./devices.js";
export type { DiagnosticsLogger, MatterLogLevel } from "./diagnostics.js";

/**
 * Default ms after an On command that an opted-in custom endpoint returns to
 * `off` — 0 = the next tick after the command commits. Config-driven via
 * `HTPC_BRIDGE_MOMENTARY_RESET_MS` (0–2000); this default must equal the tray
 * app's `momentaryResetMs` default — the two sides ship as one product.
 */
export const DEFAULT_MOMENTARY_RESET_MS = 0;

/** Irreversible Power always resets on the next tick, independent of custom timing. */
export const POWER_MOMENTARY_RESET_MS = 0;

/** Sanctioned Matter test VID/PID defaults (ADR-002); both configurable. */
export const DEFAULT_VENDOR_ID = 0xfff1;
export const DEFAULT_PRODUCT_ID = 0x8000;

/**
 * Default identity seed. Stable across restarts by construction — a changed
 * seed changes every uniqueId and Google Home re-adds all devices.
 */
const DEFAULT_UNIQUE_ID_SEED = "htpc-matter-bridge";

// S7-2 rename note: NODE_ID and the uniqueId seed are commissioning-critical
// identity (changing either re-adds every device in Google Home), so both
// deliberately keep their pre-MatterHelm values.
const NODE_ID = "htpc-bridge";
const VENDOR_NAME = "HTPC Bridge";

/**
 * Default bridge display name — the root node's nodeLabel/productName, i.e.
 * what Google Home shows for the BRIDGE itself (S10-6). Unlike NODE_ID and
 * the seed this is a plain label, not identity: changing it is safe for an
 * already-paired bridge (Google may keep showing the old name until the
 * device is re-added). Configurable so several bridges in one home are
 * tellable apart.
 */
export const DEFAULT_BRIDGE_NAME = "HTPC Matter Bridge";

/**
 * The bridge display name to publish: a trimmed non-empty override, else
 * { DEFAULT_BRIDGE_NAME}. Explicit rather than `||` so an all-whitespace
 * name can never reach Google Home as a blank device label.
 */
function resolveBridgeName(configured: string | undefined): string {
  const trimmed = configured?.trim();
  return trimmed === undefined || trimmed === "" ? DEFAULT_BRIDGE_NAME : trimmed;
}

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
   * Opt-in custom auto-reset window in ms; unset =
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
  /** Bridge display name (S10-6); unset = {@link DEFAULT_BRIDGE_NAME}. */
  bridgeName?: string;
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
   * Writes a reset-enabled custom endpoint's OnOff back to `false`.
   * `endpointId` is the Matter endpoint id (e.g. `playpause`,
   * `custom-movie-mode`); an id that names no reset-enabled endpoint throws —
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
  readonly #pending = new Map<string, { value: unknown }[]>();

  /** Records a local write and returns an idempotent cancellation callback. */
  expect(key: string, value: unknown): () => void {
    const expectation = { value };
    const queue = this.#pending.get(key);
    if (queue === undefined) {
      this.#pending.set(key, [expectation]);
    } else {
      queue.push(expectation);
    }
    return () => {
      const current = this.#pending.get(key);
      const index = current?.indexOf(expectation) ?? -1;
      if (current === undefined || index < 0) {
        return;
      }
      current.splice(index, 1);
      if (current.length === 0) {
        this.#pending.delete(key);
      }
    };
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
    if (Object.is(queue[0]?.value, value)) {
      queue.shift();
      if (queue.length === 0) {
        this.#pending.delete(key);
      }
      return true;
    }
    return false;
  }
}

interface SpeakerStateTarget {
  getLevel(): number | null;
  getOnOff(): boolean;
  setState(patch: { level?: number; onOff?: boolean }): Promise<void>;
}

/** Serializes local speaker writes so dedup reads always observe prior commits. */
export class SerializedSpeakerStateWriter {
  readonly #speaker: SpeakerStateTarget;
  readonly #suppressor: EchoSuppressor;
  #tail: Promise<void> = Promise.resolve();

  constructor(speaker: SpeakerStateTarget, suppressor: EchoSuppressor) {
    this.#speaker = speaker;
    this.#suppressor = suppressor;
  }

  async setState(level0to254: number, onOff: boolean): Promise<void> {
    const pending = this.#tail.then(async () => {
      const patch: { level?: number; onOff?: boolean } = {};
      const cancelExpectations: (() => void)[] = [];
      if (this.#speaker.getLevel() !== level0to254) {
        patch.level = level0to254;
        cancelExpectations.push(this.#suppressor.expect(SPEAKER_LEVEL, level0to254));
      }
      if (this.#speaker.getOnOff() !== onOff) {
        patch.onOff = onOff;
        cancelExpectations.push(this.#suppressor.expect(SPEAKER_ONOFF, onOff));
      }
      if (patch.level !== undefined || patch.onOff !== undefined) {
        try {
          await this.#speaker.setState(patch);
        } catch (error) {
          for (const cancel of cancelExpectations) {
            cancel();
          }
          throw error;
        }
      }
    });
    this.#tail = pending.catch(() => undefined);
    await pending;
  }
}

/**
 * Per-endpoint reset timers keyed by Matter endpoint id. Pure scheduling: the
 * injected callback owns whether the local target value is Off (custom) or On
 * (irreversible Power).
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
export type PlugEndpointKey = Exclude<BuiltinEndpointKey, "speaker" | "power">;

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
  /** Power command plus the app-derived irreversible-action policy. */
  | { kind: "powerCommand"; on: boolean; momentary: boolean }
  /** A custom command plug's OnOff command (ADR-004). */
  | { kind: "customCommand"; customKey: string; on: boolean; resetAfterActivation: boolean };

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
      return { endpoint: event.key, cluster: "onOff", on: event.on };
    }
    case "powerCommand": {
      return {
        endpoint: "power",
        cluster: "onOff",
        on: event.on,
        momentary: event.momentary,
      };
    }
    case "customCommand": {
      return {
        endpoint: "custom",
        key: event.customKey,
        cluster: "onOff",
        on: event.on,
        resetAfterActivation: event.resetAfterActivation,
      };
    }
  }
}

/**
 * Builds one plug endpoint's OnOff command handler: emit the event the
 * command means, and — only when reset policy is supplied — (re)arm or cancel
 * its auto-reset window.
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
 * Builds the Power command handler. Reversible modes only emit retained-state
 * commands. Irreversible modes emit first, then schedule the local On write:
 * dispatch gets its best chance to reach the tray before sleep tears down the
 * process, while the next-tick write remains prompt and cannot retrigger.
 */
export function makePowerCommandHandler(
  momentary: boolean,
  emit: (event: EndpointEvent) => void,
  reset?: { noteOn: () => void; noteOff: () => void },
): (on: boolean) => void {
  return (on) => {
    emit({ kind: "powerCommand", on, momentary });
    if (!momentary || reset === undefined) {
      return;
    }
    if (on) {
      reset.noteOff();
    } else {
      reset.noteOn();
    }
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
    productName: resolveBridgeName(options.bridgeName),
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
  /** The opted-in custom subset that auto-resets — the reset targets. */
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

  /** Irreversible Power reset is always next-tick; custom timing is unrelated. */
  const powerResetIds = new Set<string>();
  const resetPowerToOn = async (endpointId: string): Promise<void> => {
    const plug = powerResetIds.has(endpointId) ? plugs.get(endpointId) : undefined;
    if (plug === undefined) {
      throw new Error(`resetPowerToOn: no momentary Power endpoint ${JSON.stringify(endpointId)}`);
    }
    await plug.setOnOff(true);
  };

  /** Fired reset writes that must settle before matter.js can be closed. */
  const pendingResets = new Set<Promise<void>>();
  const trackReset = (
    endpointId: string,
    policy: "custom.resetAfterActivation" | "power.momentary",
    targetOnOff: boolean,
    reset: () => Promise<void>,
  ): void => {
    const pending = reset().catch((error: unknown) => {
      options.logger?.error(
        {
          evt: "matter.reset.error",
          endpointId,
          policy,
          targetOnOff,
          err: String(error),
        },
        "failed to reset Matter endpoint after activation",
      );
    });
    pendingResets.add(pending);
    void pending.then(() => {
      pendingResets.delete(pending);
    });
  };
  const settlePendingResets = async (): Promise<void> => {
    await Promise.all([...pendingResets]);
  };

  const scheduler = new MomentaryResetScheduler<string>(
    options.momentaryResetMs ?? DEFAULT_MOMENTARY_RESET_MS,
    (endpointId) => {
      trackReset(endpointId, "custom.resetAfterActivation", false, () =>
        resetMomentary(endpointId),
      );
    },
  );

  const powerResetScheduler = new MomentaryResetScheduler<string>(
    POWER_MOMENTARY_RESET_MS,
    (endpointId) => {
      trackReset(endpointId, "power.momentary", true, () => resetPowerToOn(endpointId));
    },
  );

  // Config-derived endpoint set: enabled built-ins in §2.2 order, then one
  // plug per custom command (endpoint numbers follow add order).
  const specs = endpointSpecs(options.endpoints, seed);
  const constructed: ConstructedEndpoint[] = [];
  let speaker: SpeakerHandle | undefined;

  /** The opted-in custom half of the command handler: its reset window. */
  const resetWindowFor = (id: string): { noteOn: () => void; noteOff: () => void } => ({
    noteOn: () => {
      scheduler.noteOn(id);
    },
    noteOff: () => {
      scheduler.noteOff(id);
    },
  });

  const powerResetWindowFor = (id: string): { noteOn: () => void; noteOff: () => void } => ({
    noteOn: () => {
      powerResetScheduler.noteOn(id);
    },
    noteOff: () => {
      powerResetScheduler.noteOff(id);
    },
  });

  try {
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
            const resetWindow = spec.resetAfterActivation ? resetWindowFor(id) : undefined;
            plugs.set(
              id,
              await node.addPlug(
                spec.info,
                makePlugCommandHandler(
                  (on) => ({
                    kind: "customCommand",
                    customKey,
                    on,
                    resetAfterActivation: spec.resetAfterActivation,
                  }),
                  emit,
                  resetWindow,
                ),
              ),
            );
            if (spec.resetAfterActivation) {
              momentaryIds.add(id);
            }
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
                makePlugCommandHandler((on) => ({ kind: "plugCommand", key, on }), emit),
              ),
            );
            break;
          }
          case "power": {
            const resetWindow = spec.momentary ? powerResetWindowFor(id) : undefined;
            plugs.set(
              id,
              await node.addPlug(
                spec.info,
                makePowerCommandHandler(spec.momentary, emit, resetWindow),
              ),
            );
            if (spec.momentary) {
              powerResetIds.add(id);
            }
            break;
          }
        }
      }
      constructed.push({ id: spec.info.id, name: spec.info.name, kind: spec.kind });
    }
  } catch (error) {
    scheduler.clear();
    powerResetScheduler.clear();
    await settlePendingResets();
    await node.close();
    throw error;
  }

  const speakerStateWriter =
    speaker === undefined ? undefined : new SerializedSpeakerStateWriter(speaker, suppressor);
  let closed = false;

  return {
    start: () => node.start(),
    close: async (): Promise<void> => {
      if (closed) {
        return;
      }
      closed = true;
      scheduler.clear();
      powerResetScheduler.clear();
      await settlePendingResets();
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
      if (speakerStateWriter === undefined) {
        return; // speaker disabled (ADR-004): tolerate state, apply nothing
      }
      await speakerStateWriter.setState(level0to254, onOff);
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
