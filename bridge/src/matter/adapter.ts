/**
 * THIN wrapper isolating matter.js (docs/BLUEPRINT.md §2.1).
 *
 * Boundary rule (CLAUDE.md invariant, reconciled for the three-file layout
 * §2.1 prescribes): this is the ONLY module in the codebase that imports
 * `@matter/*` packages. `devices.ts` and `bridge.ts` build on the wrapper
 * classes exported here and never name a matter.js type; the bridge's public
 * surface (`bridge.ts`'s `BridgeOptions` / `BridgeHandle`) is plain data, so
 * matter.js API churn stays contained in this file. The wrapper classes are
 * matter/-internal: nothing outside `src/matter/` may import this module.
 *
 * Process-global caveat: matter.js reads `storage.path` and `runtime.signals`
 * from `Environment.default`, which is shared by the whole process. This
 * adapter therefore supports ONE active {@link MatterNode} per process —
 * {@link MatterNode.create} throws if a previously created node has not been
 * closed. That matches the product shape (one sidecar = one bridge node) and
 * keeps the global mutation in a single, documented place. The matter.js
 * `Logger` statics mutated by {@link MatterNode.create} (ADR-006 §1:
 * destination replacement + levels) are process-global in the same way and,
 * like the environment vars, are not restored on close — a closing node still
 * logs, and the next node in this process reinstalls them anyway.
 */
import {
  Endpoint,
  Environment,
  LogDestination,
  LogFormat,
  Logger,
  MaybePromise,
  ServerNode,
  VendorId,
} from "@matter/main";
import { BridgedDeviceBasicInformationServer } from "@matter/main/behaviors/bridged-device-basic-information";
import {
  OnOffPlugInUnitDevice,
  OnOffPlugInUnitRequirements,
} from "@matter/main/devices/on-off-plug-in-unit";
import { SpeakerDevice } from "@matter/main/devices/speaker";
import { AggregatorEndpoint } from "@matter/main/endpoints/aggregator";

import { makeMatterLogWriter, wireSessionObservability } from "./diagnostics.js";
import type { DiagnosticsLogger, MatterLogLevel } from "./diagnostics.js";

/** Pairing codes in protocol vocabulary (ipc `pairing` frame field names). */
export interface PairingCodes {
  /** Matter onboarding payload, always `MT:`-prefixed. */
  qrPayload: string;
  /** Numeric manual pairing code (matter.js formatting). */
  manualCode: string;
}

/** Identity + label block for one bridged endpoint (built by devices.ts). */
export interface BridgedDeviceInfo {
  /** Stable Matter endpoint id (storage key) — never derived from the name. */
  id: string;
  /** Display name: nodeLabel/productName/productLabel, the voice target. */
  name: string;
  /** ≤ 32 chars; must differ from `uniqueId` (matter.js warns otherwise). */
  serialNumber: string;
  /** ≤ 32 chars; stable across restarts or Google re-adds the device. */
  uniqueId: string;
}

/** Options for {@link MatterNode.create}; all values are plain data. */
export interface MatterNodeOptions {
  /** Node id — names the storage subdirectory under `storageDir`. */
  id: string;
  /** matter.js `storage.path`; contains fabric credentials once paired. */
  storageDir: string;
  /** Matter UDP/TCP port; matter.js defaults to 5540 when omitted. */
  port?: number;
  /**
   * Pins the mDNS interface for multi-NIC hosts (maps to matter.js
   * `mdns.networkInterface`, BLUEPRINT §2.1 Windows notes). Unset = matter.js
   * auto-detects the primary LAN adapter.
   */
  mdnsInterface?: string;
  /** Vendor id (test VID per ADR-002 unless overridden). */
  vendorId: number;
  /** Product id (test PID per ADR-002 unless overridden). */
  productId: number;
  vendorName: string;
  /** Also used as productLabel and nodeLabel of the root node. */
  productName: string;
  serialNumber: string;
  /** Must differ from `serialNumber` (BLUEPRINT §2.1 facts block). */
  uniqueId: string;
  /**
   * Diagnostics seam (ADR-006 §1). When present, matter.js's console log
   * destination is replaced with one forwarding every line to this logger as
   * `{evt:"matter.log", facility, ...}` events, and session/subscription
   * lifecycle is logged as `{evt:"matter.session"|"matter.subscription"}`.
   * When absent (tests, smoke script) matter.js keeps its console logging.
   */
  logger?: DiagnosticsLogger;
  /** matter.js global log level (`Logger.level`); unset = matter.js default. */
  matterLogLevel?: MatterLogLevel;
  /** Per-facility level overrides (`Logger.facilityLevels`); unset = none. */
  matterLogFacilities?: Readonly<Record<string, MatterLogLevel>>;
}

/**
 * OnOff **command** observers for plug endpoints, keyed by Matter endpoint id
 * (ADR-008). Module-global because matter.js constructs behaviors itself —
 * {@link CommandObservingOnOffServer} has no other channel back to the code
 * that created the endpoint. Safe for the same reason the environment claim
 * above is: this adapter supports exactly ONE {@link MatterNode} per process,
 * and endpoint ids are unique within its aggregator. Cleared by
 * {@link MatterNode.close}.
 */
const plugCommandObservers = new Map<string, (on: boolean) => void>();

/**
 * The plug's OnOff server, extended so every On/Off **command invocation** is
 * observable — not just the attribute changes it produces (ADR-008).
 *
 * Why commands: Matter's `onOff` attribute is read-only, so a controller can
 * only change it by invoking `On`/`Off`/`Toggle`, and matter.js emits NO
 * `$Changed` event when a command writes the value the attribute already
 * holds. Observing changes therefore dropped repeated identical commands
 * ("turn on HTPC Next" twice inside the reset window); observing commands
 * cannot. `toggle`, `offWithEffect`, `onWithRecallGlobalScene` and
 * `onWithTimedOff` all delegate to `on()`/`off()` in matter.js's default
 * implementation, so these two overrides cover every OnOff command form.
 *
 * Extends the device type's own feature-specialized server (`Lighting`, via
 * {@link OnOffPlugInUnitRequirements}) rather than the bare `OnOffServer`, so
 * cluster features and conformance are byte-identical to the default plug.
 *
 * The observer runs AFTER the base implementation has applied the state
 * change, and — because the base implementation is synchronous — synchronously
 * inside matter.js's command transaction: it must not write matter.js state
 * (scheduling a timer or emitting plain data is fine). Local writes made
 * through {@link PlugHandle.setOnOff} bypass the cluster commands entirely, so
 * the bridge's own momentary resets never reach an observer — no echo
 * suppression is needed on this path.
 */
class CommandObservingOnOffServer extends OnOffPlugInUnitRequirements.OnOffServer {
  override on(): MaybePromise {
    return MaybePromise.then(super.on(), () => {
      plugCommandObservers.get(this.endpoint.id)?.(true);
    });
  }

  override off(): MaybePromise {
    return MaybePromise.then(super.off(), () => {
      plugCommandObservers.get(this.endpoint.id)?.(false);
    });
  }
}

const SpeakerEndpointType = SpeakerDevice.with(BridgedDeviceBasicInformationServer);
const PlugEndpointType = OnOffPlugInUnitDevice.with(
  BridgedDeviceBasicInformationServer,
  CommandObservingOnOffServer,
);

/**
 * Guard for the process-global `Environment.default` mutation — see module
 * doc. Claimed by {@link MatterNode.create}, released by {@link MatterNode.close}.
 */
let environmentClaimed = false;

/**
 * Applies the ADR-006 §1 logging configuration to matter.js's process-global
 * `Logger` statics. Levels are applied whenever configured; the console
 * destination is replaced only when a pino sink is present — raw matter.js
 * console lines must never interleave with pino's NDJSON on stdout.
 *
 * The replacement destination reuses matter.js's default `add` (filtering by
 * `level`/`facilityLevels` happens upstream in `Logger`), formats with the
 * PLAIN formatter over `message.values` ONLY — so the text carries no
 * timestamp/level/facility preamble and no ANSI codes; those fields travel
 * structured instead — and writes one pino event per line.
 */
function configureMatterLogging(options: MatterNodeOptions): void {
  if (options.matterLogLevel !== undefined) {
    Logger.level = options.matterLogLevel;
  }
  if (options.matterLogFacilities !== undefined) {
    // Spread: matter.js's setter converts the map's values in place.
    Logger.facilityLevels = { ...options.matterLogFacilities };
  }
  if (options.logger !== undefined) {
    const plain = LogFormat(LogFormat.PLAIN);
    Logger.destinations.pino = LogDestination({
      name: "pino",
      format: (message) => plain(message.values),
      write: makeMatterLogWriter(options.logger),
    });
    delete Logger.destinations.default;
  }
}

/** The BridgedDeviceBasicInformation block every bridged endpoint carries. */
function bridgedBasicInformation(info: BridgedDeviceInfo): {
  nodeLabel: string;
  productName: string;
  productLabel: string;
  serialNumber: string;
  uniqueId: string;
  reachable: boolean;
} {
  return {
    nodeLabel: info.name,
    productName: info.name,
    productLabel: info.name,
    serialNumber: info.serialNumber,
    uniqueId: info.uniqueId,
    reachable: true,
  };
}

/**
 * Opaque handle for the Speaker endpoint (OnOff = mute, LevelControl =
 * volume — BLUEPRINT §2.2). Constructed only by {@link MatterNode.addSpeaker}.
 */
export class SpeakerHandle {
  readonly #endpoint: Endpoint<typeof SpeakerEndpointType>;

  /** @internal adapter-only — callers cannot produce the parameter type. */
  constructor(endpoint: Endpoint<typeof SpeakerEndpointType>) {
    this.#endpoint = endpoint;
  }

  getOnOff(): boolean {
    return this.#endpoint.state.onOff.onOff;
  }

  getLevel(): number | null {
    return this.#endpoint.state.levelControl.currentLevel;
  }

  /**
   * Writes level and/or onOff in one matter.js transaction. Omitted fields
   * are untouched (and emit no change event).
   */
  async setState(patch: { level?: number; onOff?: boolean }): Promise<void> {
    const values: {
      levelControl?: { currentLevel: number };
      onOff?: { onOff: boolean };
    } = {};
    if (patch.level !== undefined) {
      values.levelControl = { currentLevel: patch.level };
    }
    if (patch.onOff !== undefined) {
      values.onOff = { onOff: patch.onOff };
    }
    if (values.levelControl === undefined && values.onOff === undefined) {
      return;
    }
    await this.#endpoint.set(values);
  }

  /** Fires on every OnOff change — remote (controller) AND local writes. */
  onOnOffChanged(cb: (on: boolean) => void): void {
    this.#endpoint.events.onOff.onOff$Changed.on((value) => {
      cb(value);
    });
  }

  /**
   * Fires on every LevelControl currentLevel change; `null` is matter.js's
   * "no level set" value (the attribute is nullable).
   */
  onLevelChanged(cb: (level: number | null) => void): void {
    this.#endpoint.events.levelControl.currentLevel$Changed.on((value) => {
      cb(value);
    });
  }
}

/**
 * Opaque handle for an On/Off Plug-in Unit endpoint (momentary transport
 * buttons and the stateful power switch — BLUEPRINT §2.2). Constructed only
 * by {@link MatterNode.addPlug}.
 *
 * Reads and local writes only: controller activity on a plug arrives through
 * the command observer passed to {@link MatterNode.addPlug}, not through an
 * attribute subscription (ADR-008).
 */
export class PlugHandle {
  readonly #endpoint: Endpoint<typeof PlugEndpointType>;

  /** @internal adapter-only — callers cannot produce the parameter type. */
  constructor(endpoint: Endpoint<typeof PlugEndpointType>) {
    this.#endpoint = endpoint;
  }

  getOnOff(): boolean {
    return this.#endpoint.state.onOff.onOff;
  }

  /**
   * Writes the attribute directly (the momentary auto-reset). Writing the
   * current value is a no-op, and this path invokes no cluster command, so it
   * never reaches a command observer.
   */
  async setOnOff(on: boolean): Promise<void> {
    await this.#endpoint.set({ onOff: { onOff: on } });
  }

  /**
   * Invokes the endpoint's OnOff **command** locally, exactly as a controller
   * would — including the {@link CommandObservingOnOffServer} override, so the
   * command observer fires. Product code never calls this: it exists so the
   * boot smoke script can exercise the ADR-008 dispatch path against a live
   * matter.js node without a Matter controller.
   */
  async invokeOnOff(on: boolean): Promise<void> {
    await this.#endpoint.act(async (agent) => {
      const onOff = agent.get(CommandObservingOnOffServer);
      await (on ? onOff.on() : onOff.off());
    });
  }
}

/**
 * The matter.js ServerNode + Aggregator, wrapped so no matter.js type
 * appears in any signature a caller can reach without already being inside
 * this module. One instance per process — see module doc.
 */
export class MatterNode {
  readonly #server: ServerNode;
  readonly #aggregator: Endpoint<typeof AggregatorEndpoint>;
  #started = false;
  #closed = false;

  private constructor(server: ServerNode, aggregator: Endpoint<typeof AggregatorEndpoint>) {
    this.#server = server;
    this.#aggregator = aggregator;
  }

  /**
   * Creates the ServerNode and its Aggregator endpoint. Sets the
   * process-global `storage.path` / `runtime.signals` environment variables
   * BEFORE `ServerNode.create` (spike-confirmed requirement, BLUEPRINT §2.1):
   * storage keeps fabric credentials in `options.storageDir`, and disabling
   * runtime signals leaves SIGINT ownership with the composition root.
   */
  static async create(options: MatterNodeOptions): Promise<MatterNode> {
    if (environmentClaimed) {
      throw new Error(
        "MatterNode.create: a MatterNode is already active in this process — " +
          "Environment.default (storage.path, runtime.signals) is process-global, " +
          "so only one node per process is supported; close() the existing node first",
      );
    }
    // Before ServerNode.create so storage/network boot logs already flow
    // through the pino destination (ADR-006 §1).
    configureMatterLogging(options);
    Environment.default.vars.set("storage.path", options.storageDir);
    Environment.default.vars.set("runtime.signals", false);
    if (options.mdnsInterface !== undefined) {
      Environment.default.vars.set("mdns.networkInterface", options.mdnsInterface);
    }
    const server = await ServerNode.create({
      id: options.id,
      ...(options.port === undefined ? {} : { network: { port: options.port } }),
      productDescription: {
        name: options.productName,
        deviceType: AggregatorEndpoint.deviceType,
      },
      basicInformation: {
        vendorId: VendorId(options.vendorId),
        productId: options.productId,
        vendorName: options.vendorName,
        productName: options.productName,
        productLabel: options.productName,
        nodeLabel: options.productName,
        serialNumber: options.serialNumber,
        uniqueId: options.uniqueId,
      },
    });
    if (options.logger !== undefined) {
      // Session observability (ADR-006 §1): matter.js 0.17.7's node-level
      // `SessionsBehavior` events, adapted to the plain-data seam.
      const sessions = server.events.sessions;
      wireSessionObservability(
        {
          opened: (cb) => {
            sessions.opened.on(cb);
          },
          closed: (cb) => {
            sessions.closed.on(cb);
          },
          subscriptionAdded: (cb) => {
            sessions.subscriptionAdded.on(cb);
          },
          subscriptionsChanged: (cb) => {
            sessions.subscriptionsChanged.on(cb);
          },
        },
        options.logger,
      );
    }
    const aggregator = new Endpoint(AggregatorEndpoint, { id: "aggregator" });
    await server.add(aggregator);
    environmentClaimed = true;
    return new MatterNode(server, aggregator);
  }

  /** Adds the Speaker endpoint (OnOff + LevelControl) to the aggregator. */
  async addSpeaker(info: BridgedDeviceInfo): Promise<SpeakerHandle> {
    const endpoint = new Endpoint(SpeakerEndpointType, {
      id: info.id,
      bridgedDeviceBasicInformation: bridgedBasicInformation(info),
    });
    await this.#aggregator.add(endpoint);
    return new SpeakerHandle(endpoint);
  }

  /**
   * Adds an On/Off Plug-in Unit endpoint to the aggregator.
   *
   * `onCommand` is invoked for every OnOff **command** the endpoint receives
   * (`true` for On, `false` for Off) — see {@link CommandObservingOnOffServer}
   * for why commands rather than attribute changes, and for the callback's
   * synchronous-in-transaction contract. Registered before the endpoint joins
   * the aggregator so no command can slip past it.
   */
  async addPlug(info: BridgedDeviceInfo, onCommand?: (on: boolean) => void): Promise<PlugHandle> {
    const endpoint = new Endpoint(PlugEndpointType, {
      id: info.id,
      bridgedDeviceBasicInformation: bridgedBasicInformation(info),
    });
    if (onCommand !== undefined) {
      plugCommandObservers.set(info.id, onCommand);
    }
    await this.#aggregator.add(endpoint);
    return new PlugHandle(endpoint);
  }

  /** Brings the node online (network + mDNS announcement). Call once. */
  async start(): Promise<void> {
    if (this.#started) {
      throw new Error("MatterNode.start: already started");
    }
    await this.#server.start();
    this.#started = true;
  }

  /**
   * Shuts the node down and releases the process-global environment claim.
   * Idempotent; a closed node cannot be restarted (create a new one).
   */
  async close(): Promise<void> {
    if (this.#closed) {
      return;
    }
    this.#closed = true;
    try {
      await this.#server.close();
    } finally {
      plugCommandObservers.clear();
      environmentClaimed = false;
    }
  }

  get isCommissioned(): boolean {
    return this.#server.lifecycle.isCommissioned;
  }

  /**
   * Pairing codes, mirroring the spike semantics: `null` until {@link start}
   * (matter.js throws on earlier access), and `null` once commissioned —
   * matter.js cannot mint a fresh code for an already-commissioned node
   * (upstream limitation, BLUEPRINT §2.1); re-pairing is factory-reset →
   * pair anew.
   */
  get pairingCodes(): PairingCodes | null {
    if (!this.#started || this.#closed || this.isCommissioned) {
      return null;
    }
    const { qrPairingCode, manualPairingCode } = this.#server.state.commissioning.pairingCodes;
    return { qrPayload: qrPairingCode, manualCode: manualPairingCode };
  }

  /** `cb(true)` when a controller commissions the node, `cb(false)` on removal. */
  onCommissionedChange(cb: (commissioned: boolean) => void): void {
    this.#server.lifecycle.commissioned.on(() => {
      cb(true);
    });
    this.#server.lifecycle.decommissioned.on(() => {
      cb(false);
    });
  }
}
