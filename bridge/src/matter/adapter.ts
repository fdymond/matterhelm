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
 * keeps the global mutation in a single, documented place.
 */
import { Endpoint, Environment, ServerNode, VendorId } from "@matter/main";
import { BridgedDeviceBasicInformationServer } from "@matter/main/behaviors/bridged-device-basic-information";
import { OnOffPlugInUnitDevice } from "@matter/main/devices/on-off-plug-in-unit";
import { SpeakerDevice } from "@matter/main/devices/speaker";
import { AggregatorEndpoint } from "@matter/main/endpoints/aggregator";

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
}

const SpeakerEndpointType = SpeakerDevice.with(BridgedDeviceBasicInformationServer);
const PlugEndpointType = OnOffPlugInUnitDevice.with(BridgedDeviceBasicInformationServer);

/**
 * Guard for the process-global `Environment.default` mutation — see module
 * doc. Claimed by {@link MatterNode.create}, released by {@link MatterNode.close}.
 */
let environmentClaimed = false;

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

  /** Writing the current value is a no-op (matter.js emits no event). */
  async setOnOff(on: boolean): Promise<void> {
    await this.#endpoint.set({ onOff: { onOff: on } });
  }

  /** Fires on every OnOff change — remote (controller) AND local writes. */
  onOnOffChanged(cb: (on: boolean) => void): void {
    this.#endpoint.events.onOff.onOff$Changed.on((value) => {
      cb(value);
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
    Environment.default.vars.set("storage.path", options.storageDir);
    Environment.default.vars.set("runtime.signals", false);
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

  /** Adds an On/Off Plug-in Unit endpoint to the aggregator. */
  async addPlug(info: BridgedDeviceInfo): Promise<PlugHandle> {
    const endpoint = new Endpoint(PlugEndpointType, {
      id: info.id,
      bridgedDeviceBasicInformation: bridgedBasicInformation(info),
    });
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
