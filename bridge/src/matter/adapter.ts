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
 * Process-global caveat: matter.js reads `storage.path`, `runtime.signals`,
 * and `endpoint.validation` from `Environment.default`, which is shared by
 * the whole process. This adapter therefore supports ONE active
 * {@link MatterNode} per process — {@link MatterNode.create} throws if a
 * previously created node has not been closed. That matches the product shape (one sidecar = one bridge node) and
 * keeps the global mutation in a single, documented place. The matter.js
 * `Logger` statics mutated by {@link MatterNode.create} (ADR-006 §1:
 * destination replacement + levels) are process-global in the same way and,
 * like the environment vars, are not restored on close — a closing node still
 * logs, and the next node in this process reinstalls them anyway.
 */
import { networkInterfaces } from "node:os";
import type { NetworkInterfaceInfo } from "node:os";

import {
  Endpoint,
  Environment,
  LogDestination,
  LogFormat,
  Logger,
  MaybePromise,
  ServerNode,
  StorageManager,
  VendorId,
} from "@matter/main";
import type { NetworkInterfaceDetails } from "@matter/main";
import { BasicInformationServer } from "@matter/main/behaviors/basic-information";
import { BridgedDeviceBasicInformationServer } from "@matter/main/behaviors/bridged-device-basic-information";
import {
  OnOffPlugInUnitDevice,
  OnOffPlugInUnitRequirements,
} from "@matter/main/devices/on-off-plug-in-unit";
import { SpeakerDevice } from "@matter/main/devices/speaker";
import { AggregatorEndpoint } from "@matter/main/endpoints/aggregator";
import { NodeJsNetwork } from "@matter/nodejs";

import { makeMatterLogWriter, wireSessionObservability } from "./diagnostics.js";
import type { DiagnosticsLogger, MatterLogLevel } from "./diagnostics.js";

/** Snapshot shape returned by `node:os`'s `networkInterfaces()`. */
export type NetworkInterfaceTable = Readonly<
  Record<string, readonly NetworkInterfaceInfo[] | undefined>
>;

/**
 * Maps matter.js's Windows IPv6 scope-id identifier back to its OS-friendly
 * interface name. Friendly names pass through; other identifiers do not.
 */
export function resolveFriendlyNetworkInterfaceName(
  identifier: string,
  interfaces: NetworkInterfaceTable,
): string | undefined {
  if (interfaces[identifier] !== undefined) {
    return identifier;
  }
  if (!/^\d+$/u.test(identifier)) {
    return undefined;
  }
  const scopeId = Number(identifier);
  return Object.entries(interfaces).find(([, entries]) =>
    entries?.some(({ family, scopeid }) => family === "IPv6" && scopeid === scopeId),
  )?.[0];
}

interface PatchableNodeJsNetworkClass {
  getMulticastInterfaceIpv4(identifier: string): string | undefined;
  getNetInterfaceZoneIpv6(identifier: string): string | undefined;
  prototype: {
    getIpMac(identifier: string): NetworkInterfaceDetails | undefined;
  };
}

interface WindowsMdnsPatchOptions {
  platform?: NodeJS.Platform;
  getNetworkInterfaces?: () => NetworkInterfaceTable;
  networkClass?: unknown;
}

const patchedWindowsMdnsNetworkClasses = new WeakSet<PatchableNodeJsNetworkClass>();

function captureMethod<T extends object, K extends keyof T>(target: T, key: K): T[K] {
  return target[key];
}

function isPropertyRecord(value: unknown): value is Record<PropertyKey, unknown> {
  return (typeof value === "function" || typeof value === "object") && value !== null;
}

/**
 * Fails loudly if an upstream NodeJsNetwork refactor invalidates ADR-010's
 * three wrapped symbols. Exported so tests can pin the real runtime shape
 * without mutating the process-global class.
 */
export function assertWindowsMdnsScopeIdWorkaroundCompatibility(
  networkClass: unknown = NodeJsNetwork,
): asserts networkClass is PatchableNodeJsNetworkClass {
  if (!isPropertyRecord(networkClass)) {
    throw new Error("ADR-010 workaround incompatible: NodeJsNetwork is not a class");
  }
  const prototype = networkClass.prototype;
  const prototypeRecord = isPropertyRecord(prototype) ? prototype : {};
  const missing = [
    ["prototype.getIpMac", prototypeRecord.getIpMac],
    ["getNetInterfaceZoneIpv6", networkClass.getNetInterfaceZoneIpv6],
    ["getMulticastInterfaceIpv4", networkClass.getMulticastInterfaceIpv4],
  ]
    .filter(([, value]) => typeof value !== "function")
    .map(([name]) => name);
  if (missing.length > 0) {
    throw new Error(
      `ADR-010 workaround incompatible: NodeJsNetwork no longer exposes ${missing.join(", ")}`,
    );
  }
}

/**
 * Installs the contained matter.js Windows mDNS workaround once per network
 * class. Optional dependencies are a deterministic unit-test seam.
 */
export function installWindowsMdnsScopeIdWorkaround(options: WindowsMdnsPatchOptions = {}): void {
  const platform = options.platform ?? process.platform;
  const getInterfaces = options.getNetworkInterfaces ?? networkInterfaces;
  const networkClass = options.networkClass ?? NodeJsNetwork;
  assertWindowsMdnsScopeIdWorkaroundCompatibility(networkClass);
  if (platform !== "win32" || patchedWindowsMdnsNetworkClasses.has(networkClass)) {
    return;
  }

  const friendlyName = (identifier: string) =>
    resolveFriendlyNetworkInterfaceName(identifier, getInterfaces()) ?? identifier;
  const originalGetIpMac = captureMethod(networkClass.prototype, "getIpMac");
  const originalGetNetInterfaceZoneIpv6 = captureMethod(networkClass, "getNetInterfaceZoneIpv6");
  const originalGetMulticastInterfaceIpv4 = captureMethod(
    networkClass,
    "getMulticastInterfaceIpv4",
  );

  /*
   * matter.js 0.18.0 labels inbound Windows mDNS packets with a numeric IPv6
   * scope id, but its record and response lookups expect os.networkInterfaces()
   * friendly-name keys. This drops every query before a response is sent.
   * Keep the compatibility shim at the adapter boundary; see ADR-010.
   * Upstream: https://github.com/matter-js/matter.js (unfixed as of 2026-11-15).
   */
  networkClass.prototype.getIpMac = function (identifier) {
    return originalGetIpMac.call(this, friendlyName(identifier));
  };
  networkClass.getNetInterfaceZoneIpv6 = (identifier) =>
    originalGetNetInterfaceZoneIpv6.call(networkClass, friendlyName(identifier));
  networkClass.getMulticastInterfaceIpv4 = (identifier) =>
    originalGetMulticastInterfaceIpv4.call(networkClass, friendlyName(identifier));

  patchedWindowsMdnsNetworkClasses.add(networkClass);
}

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
  /** Diagnostic-only escape hatch for the ADR-010 comparison matrix. */
  disableMdnsScopeIdWorkaround?: boolean;
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
 * ("turn on HTPC Next" twice while it already reads On); observing commands
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
 * an opted-in custom reset never reaches an observer — no echo
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
  // 0.18 selects a console format from terminal detection. Force plain here
  // before replacing the console destination so stdout remains pino NDJSON.
  Logger.format = LogFormat.PLAIN;
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

function deviceTypeIds(endpoint: Endpoint): readonly number[] {
  return endpoint.state.descriptor.deviceTypeList
    .map(({ deviceType }) => Number(deviceType))
    .sort((left, right) => left - right);
}

function serverClusterIds(endpoint: Endpoint): readonly number[] {
  return endpoint.state.descriptor.serverList.map(Number).sort((left, right) => left - right);
}

interface DeviceTypeFingerprint {
  id: number;
  revision: number;
}

function deviceTypeFingerprint(endpoint: Endpoint): readonly DeviceTypeFingerprint[] {
  return endpoint.state.descriptor.deviceTypeList
    .map(({ deviceType, revision }) => ({ id: Number(deviceType), revision }))
    .sort((left, right) => left.id - right.id || left.revision - right.revision);
}

type EndpointValidationMode = "off" | "warn" | "strict";

function isEndpointValidationMode(value: string): value is EndpointValidationMode {
  return value === "off" || value === "warn" || value === "strict";
}

function configureEndpointValidation(
  logger: DiagnosticsLogger | undefined,
): EndpointValidationMode {
  const configured = process.env.MATTER_ENDPOINT_VALIDATION;
  if (configured === undefined) {
    return "warn";
  }
  if (isEndpointValidationMode(configured)) {
    return configured;
  }

  logger?.warn(
    {
      evt: "matter.endpoint-validation.invalid",
      value: configured,
      fallback: "warn",
    },
    "invalid MATTER_ENDPOINT_VALIDATION; forcing warn mode",
  );
  return "warn";
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

  /** Matter endpoint number, exposed as plain data for persistence tests. */
  get endpointNumber(): number {
    return Number(this.#endpoint.number);
  }

  /** Descriptor device types as plain numeric ids. */
  get deviceTypes(): readonly number[] {
    return deviceTypeIds(this.#endpoint);
  }

  /** Descriptor server clusters as plain numeric ids. */
  get serverClusters(): readonly number[] {
    return serverClusterIds(this.#endpoint);
  }

  /** Speaker LevelControl limits advertised to controllers. */
  get levelLimits(): Readonly<{ min: number; max: number }> {
    const { minLevel, maxLevel } = this.#endpoint.state.levelControl;
    if (minLevel === undefined || maxLevel === undefined) {
      throw new Error("Speaker LevelControl limits are unexpectedly absent");
    }
    return { min: minLevel, max: maxLevel };
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
 * Opaque handle for an On/Off Plug-in Unit endpoint (transport, custom, and
 * power switches — BLUEPRINT §2.2). Constructed only
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

  /** Matter endpoint number, exposed as plain data for persistence tests. */
  get endpointNumber(): number {
    return Number(this.#endpoint.number);
  }

  /** Descriptor device types as plain numeric ids. */
  get deviceTypes(): readonly number[] {
    return deviceTypeIds(this.#endpoint);
  }

  /** Descriptor server clusters as plain numeric ids. */
  get serverClusters(): readonly number[] {
    return serverClusterIds(this.#endpoint);
  }

  /**
   * Writes the attribute directly (used by opt-in custom auto-reset). Writing the
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
  readonly #publishedEndpoints = new Map<string, Endpoint>();
  readonly #endpointValidationMode: EndpointValidationMode;
  #started = false;
  #closed = false;

  private constructor(
    server: ServerNode,
    aggregator: Endpoint<typeof AggregatorEndpoint>,
    endpointValidationMode: EndpointValidationMode,
  ) {
    this.#server = server;
    this.#aggregator = aggregator;
    this.#endpointValidationMode = endpointValidationMode;
  }

  /**
   * Creates the ServerNode and its Aggregator endpoint. Sets the
   * process-global `storage.path`, `runtime.signals`, and
   * `endpoint.validation` environment variables BEFORE `ServerNode.create`
   * (spike-confirmed requirement, BLUEPRINT §2.1): storage keeps fabric
   * credentials in `options.storageDir`, disabling runtime signals leaves
   * SIGINT ownership with the composition root, and the validation write
   * overrides matter.js's import-time process-environment snapshot.
   */
  static async create(options: MatterNodeOptions): Promise<MatterNode> {
    if (environmentClaimed) {
      throw new Error(
        "MatterNode.create: a MatterNode is already active in this process — " +
          "Environment.default (storage.path, runtime.signals, endpoint.validation) is process-global, " +
          "so only one node per process is supported; close() the existing node first",
      );
    }
    // Before ServerNode.create so storage/network boot logs already flow
    // through the pino destination (ADR-006 §1).
    configureMatterLogging(options);
    const endpointValidationMode = configureEndpointValidation(options.logger);
    if (options.disableMdnsScopeIdWorkaround === true) {
      options.logger?.warn(
        { evt: "matter.mdns-scopeid-workaround.disabled" },
        "ADR-010 mDNS scope-id workaround disabled for diagnostics only",
      );
    } else {
      installWindowsMdnsScopeIdWorkaround();
    }
    Environment.default.vars.set("storage.path", options.storageDir);
    Environment.default.vars.set("runtime.signals", false);
    Environment.default.vars.set("endpoint.validation", endpointValidationMode);
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
    try {
      if (options.logger !== undefined) {
        // Session observability (ADR-006 §1): matter.js 0.18.0's node-level
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
      return new MatterNode(server, aggregator, endpointValidationMode);
    } catch (error) {
      await server.close();
      throw error;
    }
  }

  /** Adds the Speaker endpoint (OnOff + LevelControl) to the aggregator. */
  async addSpeaker(info: BridgedDeviceInfo): Promise<SpeakerHandle> {
    const endpoint = new Endpoint(SpeakerEndpointType, {
      id: info.id,
      bridgedDeviceBasicInformation: bridgedBasicInformation(info),
      // The non-lighting Speaker permits 0-254. State the fixed limits
      // explicitly so future matter.js defaults cannot alter its contract.
      levelControl: { minLevel: 0, maxLevel: 254 },
    });
    await this.#aggregator.add(endpoint);
    this.#publishedEndpoints.set(info.id, endpoint);
    return new SpeakerHandle(endpoint);
  }

  /**
   * Adds an On/Off Plug-in Unit endpoint to the aggregator.
   *
   * `onCommand` is invoked for every OnOff **command** the endpoint receives
   * (`true` for On, `false` for Off) — see {@link CommandObservingOnOffServer}
   * for why commands rather than attribute changes, and for the callback's
   * synchronous-in-transaction contract. Registration follows a successful
   * add; nodes are assembled before start, so no controller command can race it.
   */
  async addPlug(info: BridgedDeviceInfo, onCommand?: (on: boolean) => void): Promise<PlugHandle> {
    const endpoint = new Endpoint(PlugEndpointType, {
      id: info.id,
      bridgedDeviceBasicInformation: bridgedBasicInformation(info),
    });
    await this.#aggregator.add(endpoint);
    this.#publishedEndpoints.set(info.id, endpoint);
    if (onCommand !== undefined) {
      plugCommandObservers.set(info.id, onCommand);
    }
    return new PlugHandle(endpoint);
  }

  /**
   * Persists the actual endpoint/device-type/cluster composition and increments
   * BasicInformation.ConfigurationVersion once when it changed since the prior
   * run. An uncommissioned first run establishes a baseline; a commissioned
   * node without our fingerprint is an upgrade and increments once.
   */
  async synchronizeEndpointConfiguration(): Promise<{
    changed: boolean;
    configurationVersion: number;
  }> {
    const endpoints = [
      { id: "$root", endpoint: this.#server },
      { id: "$aggregator", endpoint: this.#aggregator },
      ...[...this.#publishedEndpoints.entries()].map(([id, endpoint]) => ({ id, endpoint })),
    ]
      .map(({ id, endpoint }) => ({
        id,
        deviceTypes: deviceTypeFingerprint(endpoint),
        serverClusters: serverClusterIds(endpoint),
      }))
      .sort((left, right) => (left.id < right.id ? -1 : left.id > right.id ? 1 : 0));
    const fingerprint = JSON.stringify({ version: 2, endpoints });
    const storage = this.#server.env.get(StorageManager).createContext("matterhelm");
    const previous = (await storage.has("endpoint-composition"))
      ? await storage.get<string>("endpoint-composition")
      : undefined;
    if (previous === undefined) {
      if (this.isCommissioned) {
        await this.#server.act(async (agent) => {
          await agent.get(BasicInformationServer).increaseConfigurationVersion();
        });
        await storage.set("endpoint-composition", fingerprint);
        return { changed: true, configurationVersion: this.configurationVersion };
      }
      await storage.set("endpoint-composition", fingerprint);
      return { changed: false, configurationVersion: this.configurationVersion };
    }
    if (previous === fingerprint) {
      return { changed: false, configurationVersion: this.configurationVersion };
    }

    await this.#server.act(async (agent) => {
      await agent.get(BasicInformationServer).increaseConfigurationVersion();
    });
    await storage.set("endpoint-composition", fingerprint);
    return { changed: true, configurationVersion: this.configurationVersion };
  }

  /** Root BasicInformation.ConfigurationVersion as plain data. */
  get configurationVersion(): number {
    return this.#server.state.basicInformation.configurationVersion;
  }

  /** Effective matter.js endpoint-validation mode selected before node creation. */
  get endpointValidationMode(): EndpointValidationMode {
    return this.#endpointValidationMode;
  }

  /** Aggregator Descriptor server clusters as plain numeric ids. */
  get aggregatorServerClusters(): readonly number[] {
    return serverClusterIds(this.#aggregator);
  }

  /** Root-only clusters added by the 0.18 ServerNode composition, as plain ids. */
  get rootServerClusters(): readonly number[] {
    return serverClusterIds(this.#server);
  }

  /** Whether the 0.18 root AccessControl composition exposes AuxiliaryAcl. */
  get hasRootAuxiliaryAcl(): boolean {
    return this.#server.state.accessControl.auxiliaryAcl !== undefined;
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
