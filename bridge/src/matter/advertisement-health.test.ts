import type { NetworkInterfaceInfo } from "node:os";

import { afterEach, describe, expect, it, vi } from "vitest";

import {
  AdvertisementHealthMonitor,
  AdvertisementHealthMonitorLifecycle,
  containsMatterCommissionableSrv,
  containsMatterCommissionableService,
  makeMatterQuery,
  probeMatterAdvertisement,
  selectIpv4Address,
} from "./advertisement-health.js";

describe("mDNS advertisement health", () => {
  afterEach(() => vi.useRealTimers());

  it("builds a PTR question for _matterc._udp.local", () => {
    const query = makeMatterQuery();
    expect(query.readUInt16BE(4)).toBe(1);
    expect(query.includes(Buffer.from("_matterc"))).toBe(true);
    expect(query.readUInt16BE(query.length - 4)).toBe(12);
    expect(query.readUInt16BE(query.length - 2)).toBe(1);
  });

  it("requires an SRV record for this bridge's Matter port", () => {
    const owner = Buffer.from([
      8,
      ...Buffer.from("_matterc"),
      4,
      ...Buffer.from("_udp"),
      5,
      ...Buffer.from("local"),
      0,
    ]);
    const header = Buffer.alloc(12);
    header.writeUInt16BE(1, 6);
    const fixed = Buffer.alloc(16);
    fixed.writeUInt16BE(33, 0);
    fixed.writeUInt16BE(1, 2);
    fixed.writeUInt16BE(6, 8);
    fixed.writeUInt16BE(5543, 14);
    const response = Buffer.concat([header, owner, fixed]);
    expect(containsMatterCommissionableSrv(response, 5543)).toBe(true);
    expect(containsMatterCommissionableSrv(response, 5540)).toBe(false);
  });

  it("recognizes commissionable service packets and rejects unrelated mDNS", () => {
    expect(containsMatterCommissionableService(makeMatterQuery())).toBe(true);
    expect(containsMatterCommissionableService(Buffer.from("_googlezone._tcp.local"))).toBe(false);
  });

  function ipv4(address: string): NetworkInterfaceInfo {
    return {
      address,
      netmask: "255.255.255.0",
      family: "IPv4",
      mac: "00:00:00:00:00:00",
      internal: false,
      cidr: `${address}/24`,
    };
  }

  it("selects the interface chosen by the OS default IPv4 route", async () => {
    const interfaces: NodeJS.Dict<NetworkInterfaceInfo[]> = {
      "vEthernet (WSL)": [ipv4("203.0.113.201")],
      Ethernet: [ipv4("192.0.2.20")],
    };
    await expect(
      selectIpv4Address(undefined, {
        interfaces,
        defaultRouteAddress: () => Promise.resolve("192.0.2.20"),
      }),
    ).resolves.toBe("192.0.2.20");
  });

  it("falls back to the first external IPv4 when default-route discovery fails", async () => {
    const interfaces: NodeJS.Dict<NetworkInterfaceInfo[]> = {
      Ethernet: [ipv4("192.0.2.20")],
      VPN: [ipv4("203.0.113.2")],
    };
    await expect(
      selectIpv4Address(undefined, {
        interfaces,
        defaultRouteAddress: () => Promise.reject(new Error("route unavailable")),
      }),
    ).resolves.toBe("192.0.2.20");
  });

  it.each([
    ["IPv4", "visible", "missing"],
    ["IPv6", "missing", "visible"],
  ] as const)("reports visible when only %s observes the advertisement", async (_, ipv4, ipv6) => {
    const probeIpv4 = vi.fn(() => Promise.resolve(ipv4));
    const probeIpv6 = vi.fn(() => Promise.resolve(ipv6));

    await expect(
      probeMatterAdvertisement(
        { expectedMatterPort: 5540 },
        {
          probeIpv4,
          probeIpv6,
        },
      ),
    ).resolves.toBe("visible");
    expect(probeIpv4).toHaveBeenCalledOnce();
    expect(probeIpv6).toHaveBeenCalledOnce();
  });

  it("runs both families within one shared timeout budget", async () => {
    vi.useFakeTimers();
    const starts: string[] = [];
    const pendingProbe = (family: string) =>
      vi.fn(
        (_options: { expectedMatterPort: number }, signal: AbortSignal) =>
          new Promise<"missing">((resolve) => {
            starts.push(family);
            signal.addEventListener(
              "abort",
              () => {
                resolve("missing");
              },
              { once: true },
            );
          }),
      );
    const probeIpv4 = pendingProbe("IPv4");
    const probeIpv6 = pendingProbe("IPv6");
    const result = probeMatterAdvertisement(
      { expectedMatterPort: 5540, timeoutMs: 7000 },
      { probeIpv4, probeIpv6 },
    );
    let completed = false;
    void result.then(() => {
      completed = true;
    });

    expect(starts).toEqual(["IPv4", "IPv6"]);
    await vi.advanceTimersByTimeAsync(6999);
    expect(completed).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    await expect(result).resolves.toBe("missing");
  });

  it("reports transitions, suppresses repeats, and stops polling after close", async () => {
    vi.useFakeTimers();
    const results = ["visible", "visible", "missing"] as const;
    let invocation = 0;
    const changes: string[] = [];
    const monitor = new AdvertisementHealthMonitor({
      probe: () =>
        Promise.resolve(results[Math.min(invocation++, results.length - 1)] ?? "missing"),
      onChange: (health) => {
        changes.push(health);
      },
      intervalMs: 1000,
    });
    monitor.start();
    await vi.advanceTimersByTimeAsync(0);
    expect(changes).toEqual(["visible"]);
    await vi.advanceTimersByTimeAsync(2000);
    expect(changes).toEqual(["visible", "missing"]);
    monitor.close();
    await vi.advanceTimersByTimeAsync(5000);
    expect(invocation).toBe(3);
  });

  it("does not create a second poll loop when start is called during the first probe", async () => {
    vi.useFakeTimers();
    let finishProbe: (health: "visible") => void = () => undefined;
    const probe = vi.fn(
      () =>
        new Promise<"visible">((resolve) => {
          finishProbe = resolve;
        }),
    );
    const monitor = new AdvertisementHealthMonitor({
      probe,
      onChange: () => undefined,
      intervalMs: 1000,
    });
    monitor.start();
    monitor.start();
    expect(probe).toHaveBeenCalledTimes(1);
    finishProbe("visible");
    await vi.advanceTimersByTimeAsync(1000);
    expect(probe).toHaveBeenCalledTimes(2);
    monitor.close();
  });

  it("closes on commission and reports visible again from a fresh monitor after unpair", async () => {
    vi.useFakeTimers();
    const changes: string[] = [];
    const probe = vi.fn(() => Promise.resolve<"visible">("visible"));
    const lifecycle = new AdvertisementHealthMonitorLifecycle(
      () =>
        new AdvertisementHealthMonitor({
          probe,
          onChange: (health) => changes.push(health),
          intervalMs: 1000,
        }),
    );

    lifecycle.setCommissioned(false);
    await vi.advanceTimersByTimeAsync(0);
    expect(changes).toEqual(["visible"]);
    lifecycle.setCommissioned(true);
    await vi.advanceTimersByTimeAsync(5000);
    expect(probe).toHaveBeenCalledTimes(1);

    lifecycle.setCommissioned(false);
    await vi.advanceTimersByTimeAsync(0);
    expect(changes).toEqual(["visible", "visible"]);
    expect(probe).toHaveBeenCalledTimes(2);
    lifecycle.close();
  });

  it("treats duplicate commissioned state as a no-op", async () => {
    vi.useFakeTimers();
    const probe = vi.fn(() => Promise.resolve<"visible">("visible"));
    const createMonitor = vi.fn(
      () =>
        new AdvertisementHealthMonitor({
          probe,
          onChange: () => undefined,
          intervalMs: 1000,
        }),
    );
    const lifecycle = new AdvertisementHealthMonitorLifecycle(createMonitor);

    lifecycle.setCommissioned(false);
    lifecycle.setCommissioned(false);
    await vi.advanceTimersByTimeAsync(0);
    expect(createMonitor).toHaveBeenCalledOnce();
    expect(probe).toHaveBeenCalledOnce();

    lifecycle.setCommissioned(true);
    lifecycle.setCommissioned(true);
    await vi.advanceTimersByTimeAsync(5000);
    expect(createMonitor).toHaveBeenCalledOnce();
    expect(probe).toHaveBeenCalledOnce();
    lifecycle.close();
  });
});
