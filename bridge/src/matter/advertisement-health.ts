import { createSocket } from "node:dgram";
import { networkInterfaces } from "node:os";
import type { NetworkInterfaceInfo } from "node:os";

const MDNS_ADDRESS = "224.0.0.251";
const MDNS_ADDRESS_IPV6 = "ff02::fb";
const MDNS_PORT = 5353;
const MATTER_COMMISSIONABLE_SERVICE = "_matterc._udp.local";

/** Result surfaced across the matter boundary; no matter.js type escapes. */
export type AdvertisementHealth = "visible" | "missing";

export interface AdvertisementProbeOptions {
  interfaceName?: string;
  expectedMatterPort: number;
  timeoutMs?: number;
}

type AdvertisementFamilyProbe = (
  options: AdvertisementProbeOptions,
  signal: AbortSignal,
) => Promise<AdvertisementHealth>;

/** Injectable family probes keep aggregation and timeout behavior deterministic in tests. */
export interface AdvertisementProbeDependencies {
  probeIpv4?: AdvertisementFamilyProbe;
  probeIpv6?: AdvertisementFamilyProbe;
}

function encodeDnsName(name: string): Buffer {
  const parts = name
    .split(".")
    .flatMap((label) => [Buffer.from([label.length]), Buffer.from(label)]);
  return Buffer.concat([...parts, Buffer.from([0])]);
}

/** Builds an RFC 6762 PTR query for the commissionable Matter service. */
export function makeMatterQuery(): Buffer {
  const header = Buffer.alloc(12);
  header.writeUInt16BE(1, 4);
  const question = Buffer.alloc(4);
  question.writeUInt16BE(12, 0);
  question.writeUInt16BE(1, 2);
  return Buffer.concat([header, encodeDnsName(MATTER_COMMISSIONABLE_SERVICE), question]);
}

/** True when a DNS packet names the commissionable Matter service. */
export function containsMatterCommissionableService(packet: Uint8Array): boolean {
  const label = Buffer.from("_matterc");
  return Buffer.from(packet).includes(Buffer.concat([Buffer.from([label.length]), label]));
}

function skipDnsName(packet: Buffer, initialOffset: number): number | undefined {
  let offset = initialOffset;
  for (let labels = 0; labels < 128; labels += 1) {
    const length = packet[offset];
    if (length === undefined) return undefined;
    if (length === 0) return offset + 1;
    if ((length & 0xc0) === 0xc0) {
      return packet[offset + 1] === undefined ? undefined : offset + 2;
    }
    if ((length & 0xc0) !== 0 || offset + 1 + length > packet.length) return undefined;
    offset += 1 + length;
  }
  return undefined;
}

/** True when a response contains this bridge's commissionable SRV record. */
export function containsMatterCommissionableSrv(
  packetBytes: Uint8Array,
  expectedMatterPort: number,
): boolean {
  const packet = Buffer.from(packetBytes);
  if (packet.length < 12 || !containsMatterCommissionableService(packet)) return false;

  let offset = 12;
  const questions = packet.readUInt16BE(4);
  for (let index = 0; index < questions; index += 1) {
    const afterName = skipDnsName(packet, offset);
    if (afterName === undefined || afterName + 4 > packet.length) return false;
    offset = afterName + 4;
  }

  const records = packet.readUInt16BE(6) + packet.readUInt16BE(8) + packet.readUInt16BE(10);
  for (let index = 0; index < records; index += 1) {
    const afterName = skipDnsName(packet, offset);
    if (afterName === undefined || afterName + 10 > packet.length) return false;
    const type = packet.readUInt16BE(afterName);
    const dataLength = packet.readUInt16BE(afterName + 8);
    const dataOffset = afterName + 10;
    if (dataOffset + dataLength > packet.length) return false;
    if (
      type === 33 &&
      dataLength >= 6 &&
      packet.readUInt16BE(dataOffset + 4) === expectedMatterPort
    ) {
      return true;
    }
    offset = dataOffset + dataLength;
  }
  return false;
}

type InterfaceTable = NodeJS.Dict<NetworkInterfaceInfo[]>;

interface Ipv6ProbeInterface {
  addresses: ReadonlySet<string>;
  membershipInterface: string;
  destination: string;
}

function defaultRouteIpv4Address(): Promise<string> {
  return new Promise((resolve, reject) => {
    const socket = createSocket("udp4");
    socket.once("error", (error) => {
      socket.close();
      reject(error);
    });
    // UDP connect selects a route and local address without sending a packet.
    socket.connect(53, "8.8.8.8", () => {
      const address = socket.address().address;
      socket.close();
      resolve(address);
    });
  });
}

/** Selects the explicit interface, or the OS-default routed IPv4 with a safe fallback. */
export async function selectIpv4Address(
  interfaceName?: string,
  dependencies: {
    interfaces?: InterfaceTable;
    defaultRouteAddress?: () => Promise<string>;
  } = {},
): Promise<string> {
  const interfaces = dependencies.interfaces ?? networkInterfaces();
  const selected =
    interfaceName === undefined
      ? Object.values(interfaces).flatMap((addresses) => addresses ?? [])
      : (interfaces[interfaceName] ?? []);
  const candidates = selected.filter(
    (candidate) => candidate.family === "IPv4" && !candidate.internal,
  );
  const fallback = candidates[0]?.address;
  if (fallback === undefined) {
    throw new Error(
      interfaceName === undefined
        ? "mDNS health check: no external IPv4 interface is available"
        : `mDNS health check: interface "${interfaceName}" has no external IPv4 address`,
    );
  }
  if (interfaceName !== undefined) return fallback;

  try {
    const routed = await (dependencies.defaultRouteAddress ?? defaultRouteIpv4Address)();
    return candidates.some((candidate) => candidate.address === routed) ? routed : fallback;
  } catch {
    // Route discovery is advisory; retain the pre-S10-15 first-interface behavior on failure.
    return fallback;
  }
}

function selectIpv6Interfaces(
  interfaceName: string | undefined,
  interfaces: InterfaceTable = networkInterfaces(),
): Ipv6ProbeInterface[] {
  const selected =
    interfaceName === undefined
      ? Object.entries(interfaces)
      : [[interfaceName, interfaces[interfaceName]] as const];
  const result = selected.flatMap(([name, addresses]) => {
    const candidates = (addresses ?? []).filter(
      (candidate) => candidate.family === "IPv6" && !candidate.internal,
    );
    if (candidates.length === 0) return [];

    const zone =
      process.platform === "win32"
        ? candidates.find((candidate) => candidate.address.toLowerCase().startsWith("fe80::"))
            ?.scopeid
        : name;
    if (zone === undefined) return [];

    return [
      {
        addresses: new Set(candidates.map((candidate) => candidate.address.toLowerCase())),
        membershipInterface: `::%${String(zone)}`,
        destination: `${MDNS_ADDRESS_IPV6}%${String(zone)}`,
      },
    ];
  });
  if (result.length === 0) {
    throw new Error(
      interfaceName === undefined
        ? "mDNS health check: no external IPv6 multicast interface is available"
        : `mDNS health check: interface "${interfaceName}" has no external IPv6 multicast address`,
    );
  }
  return result;
}

function stripIpv6Zone(address: string): string {
  return (address.split("%", 1)[0] ?? address).toLowerCase();
}

function probeIpv4Advertisement(
  options: AdvertisementProbeOptions,
  signal: AbortSignal,
): Promise<AdvertisementHealth> {
  return selectIpv4Address(options.interfaceName).then(
    (interfaceAddress) =>
      new Promise((resolve, reject) => {
        if (signal.aborted) {
          resolve("missing");
          return;
        }
        const socket = createSocket({ type: "udp4", reuseAddr: true });
        let settled = false;
        let retry: NodeJS.Timeout | undefined;
        const finish = (result: AdvertisementHealth | Error): void => {
          if (settled) return;
          settled = true;
          if (retry !== undefined) clearTimeout(retry);
          signal.removeEventListener("abort", abort);
          socket.close();
          if (result instanceof Error) {
            reject(result);
          } else {
            resolve(result);
          }
        };
        const abort = (): void => {
          finish("missing");
        };
        signal.addEventListener("abort", abort, { once: true });
        socket.on("error", (error) => {
          finish(error);
        });
        socket.on("message", (packet, remote) => {
          if (
            remote.address === interfaceAddress &&
            containsMatterCommissionableSrv(packet, options.expectedMatterPort)
          ) {
            finish("visible");
          }
        });
        socket.bind(MDNS_PORT, "0.0.0.0", () => {
          try {
            socket.addMembership(MDNS_ADDRESS, interfaceAddress);
            socket.setMulticastInterface(interfaceAddress);
            const query = makeMatterQuery();
            socket.send(query, MDNS_PORT, MDNS_ADDRESS);
            retry = setTimeout(() => {
              if (!settled) socket.send(query, MDNS_PORT, MDNS_ADDRESS);
            }, 1200);
            retry.unref();
          } catch (error) {
            finish(error instanceof Error ? error : new Error(String(error)));
          }
        });
      }),
  );
}

function probeIpv6Advertisement(
  options: AdvertisementProbeOptions,
  signal: AbortSignal,
): Promise<AdvertisementHealth> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) {
      resolve("missing");
      return;
    }
    let interfaces: Ipv6ProbeInterface[];
    try {
      interfaces = selectIpv6Interfaces(options.interfaceName);
    } catch (error) {
      reject(error instanceof Error ? error : new Error(String(error)));
      return;
    }

    const localAddresses = new Set(interfaces.flatMap(({ addresses }) => [...addresses]));
    const socket = createSocket({ type: "udp6", reuseAddr: true, ipv6Only: true });
    let settled = false;
    let retry: NodeJS.Timeout | undefined;
    const finish = (result: AdvertisementHealth | Error): void => {
      if (settled) return;
      settled = true;
      if (retry !== undefined) clearTimeout(retry);
      signal.removeEventListener("abort", abort);
      socket.close();
      if (result instanceof Error) {
        reject(result);
      } else {
        resolve(result);
      }
    };
    const abort = (): void => {
      finish("missing");
    };
    signal.addEventListener("abort", abort, { once: true });
    socket.on("error", (error) => {
      finish(error);
    });
    socket.on("message", (packet, remote) => {
      if (
        localAddresses.has(stripIpv6Zone(remote.address)) &&
        containsMatterCommissionableSrv(packet, options.expectedMatterPort)
      ) {
        finish("visible");
      }
    });
    socket.bind(MDNS_PORT, "::", () => {
      try {
        for (const probeInterface of interfaces) {
          socket.addMembership(MDNS_ADDRESS_IPV6, probeInterface.membershipInterface);
        }
        if (options.interfaceName !== undefined) {
          socket.setMulticastInterface(interfaces[0]?.membershipInterface ?? "");
        }
        const query = makeMatterQuery();
        const sendQueries = (): void => {
          for (const probeInterface of interfaces) {
            socket.send(query, MDNS_PORT, probeInterface.destination);
          }
        };
        sendQueries();
        retry = setTimeout(() => {
          if (!settled) sendQueries();
        }, 1200);
        retry.unref();
      } catch (error) {
        finish(error instanceof Error ? error : new Error(String(error)));
      }
    });
  });
}

/**
 * Observes the same local mDNS receive path a commissioner relies on.
 *
 * This deliberately uses a short-lived SO_REUSEADDR listener: a successful
 * matter.js send callback only proves that Windows accepted a datagram, while
 * the launch-blocking failure produced no observable record at all. The probe
 * is short-lived and sends two active PTR questions before deciding.
 *
 * IPv4 and IPv6 listen concurrently within one deadline, and IPv6 joins each
 * eligible interface using the same zone-scoped form as matter.js. This is
 * still a local self-observation: `visible` proves this host can receive its
 * SRV advertisement, not that multicast crosses every AP/VLAN to a controller.
 */
export async function probeMatterAdvertisement(
  { interfaceName, expectedMatterPort, timeoutMs = 7000 }: AdvertisementProbeOptions,
  dependencies: AdvertisementProbeDependencies = {},
): Promise<AdvertisementHealth> {
  const options: AdvertisementProbeOptions = {
    ...(interfaceName === undefined ? {} : { interfaceName }),
    expectedMatterPort,
    timeoutMs,
  };
  const controller = new AbortController();
  return new Promise((resolve) => {
    let settled = false;
    let missingFamilies = 0;
    const finish = (health: AdvertisementHealth): void => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      controller.abort();
      resolve(health);
    };
    const timeout = setTimeout(() => {
      finish("missing");
    }, timeoutMs);
    const observe = (probe: AdvertisementFamilyProbe): void => {
      probe(options, controller.signal).then(
        (health) => {
          if (health === "visible") {
            finish("visible");
            return;
          }
          missingFamilies += 1;
          if (missingFamilies === 2) finish("missing");
        },
        () => {
          missingFamilies += 1;
          if (missingFamilies === 2) finish("missing");
        },
      );
    };
    observe(dependencies.probeIpv4 ?? probeIpv4Advertisement);
    observe(dependencies.probeIpv6 ?? probeIpv6Advertisement);
  });
}

/** Periodically reports advertisement health, suppressing unchanged repeats. */
export class AdvertisementHealthMonitor {
  readonly #probe: () => Promise<AdvertisementHealth>;
  readonly #onChange: (health: AdvertisementHealth) => void;
  readonly #intervalMs: number;
  #timer: NodeJS.Timeout | undefined;
  #last?: AdvertisementHealth;
  #closed = false;
  #running = false;

  constructor(options: {
    probe: () => Promise<AdvertisementHealth>;
    onChange: (health: AdvertisementHealth) => void;
    intervalMs?: number;
  }) {
    this.#probe = options.probe;
    this.#onChange = options.onChange;
    this.#intervalMs = options.intervalMs ?? 60_000;
  }

  start(): void {
    if (this.#timer !== undefined || this.#running || this.#closed) return;
    this.#running = true;
    void this.#run();
  }

  close(): void {
    this.#closed = true;
    if (this.#timer !== undefined) clearTimeout(this.#timer);
    this.#timer = undefined;
  }

  async #run(): Promise<void> {
    try {
      let health: AdvertisementHealth;
      try {
        health = await this.#probe();
      } catch {
        health = "missing";
      }
      if (!this.#closed && health !== this.#last) {
        this.#last = health;
        this.#onChange(health);
      }
    } finally {
      this.#running = false;
      if (!this.#closed) {
        this.#timer = setTimeout(() => {
          this.#timer = undefined;
          this.start();
        }, this.#intervalMs);
        this.#timer.unref();
      }
    }
  }
}

/** Owns the one advertisement monitor that is useful only while uncommissioned. */
export class AdvertisementHealthMonitorLifecycle {
  readonly #createMonitor: () => AdvertisementHealthMonitor;
  #monitor: AdvertisementHealthMonitor | undefined;
  #commissioned: boolean | undefined;

  constructor(createMonitor: () => AdvertisementHealthMonitor) {
    this.#createMonitor = createMonitor;
  }

  /** Stops polling when commissioned; decommissioning starts a fresh dedup state. */
  setCommissioned(commissioned: boolean): void {
    if (commissioned === this.#commissioned) return;
    this.#commissioned = commissioned;
    this.#monitor?.close();
    this.#monitor = undefined;
    if (!commissioned) {
      this.#monitor = this.#createMonitor();
      this.#monitor.start();
    }
  }

  close(): void {
    this.#monitor?.close();
    this.#monitor = undefined;
  }
}
