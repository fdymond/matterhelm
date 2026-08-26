import type { NetworkInterfaceInfo } from "node:os";

import { describe, expect, it } from "vitest";

import {
  installWindowsMdnsScopeIdWorkaround,
  resolveFriendlyNetworkInterfaceName,
  type NetworkInterfaceTable,
} from "./adapter.js";

const IPV4: NetworkInterfaceInfo = {
  address: "192.168.1.20",
  netmask: "255.255.255.0",
  family: "IPv4",
  mac: "00:11:22:33:44:55",
  internal: false,
  cidr: "192.168.1.20/24",
};

const IPV6_SCOPE_5: NetworkInterfaceInfo = {
  address: "fe80::1234",
  netmask: "ffff:ffff:ffff:ffff::",
  family: "IPv6",
  mac: "00:11:22:33:44:55",
  internal: false,
  cidr: "fe80::1234/64",
  scopeid: 5,
};

const INTERFACES: NetworkInterfaceTable = {
  Ethernet: [IPV4],
  "Wi-Fi": [IPV4, IPV6_SCOPE_5],
};

describe("resolveFriendlyNetworkInterfaceName", () => {
  it("passes through an existing friendly interface name", () => {
    expect(resolveFriendlyNetworkInterfaceName("Wi-Fi", INTERFACES)).toBe("Wi-Fi");
  });

  it("resolves a numeric IPv6 scope id to the friendly interface name", () => {
    expect(resolveFriendlyNetworkInterfaceName("5", INTERFACES)).toBe("Wi-Fi");
  });

  it("returns undefined for an unknown numeric scope id", () => {
    expect(resolveFriendlyNetworkInterfaceName("99", INTERFACES)).toBeUndefined();
  });

  it("returns undefined for a non-numeric unknown identifier", () => {
    expect(resolveFriendlyNetworkInterfaceName("VPN", INTERFACES)).toBeUndefined();
  });
});

describe("installWindowsMdnsScopeIdWorkaround", () => {
  function makeNetworkClass() {
    return class FakeNetwork {
      static zoneCalls: string[] = [];
      static multicastCalls: string[] = [];
      ipMacCalls: string[] = [];

      static getNetInterfaceZoneIpv6(identifier: string): string {
        this.zoneCalls.push(identifier);
        return `zone:${identifier}`;
      }

      static getMulticastInterfaceIpv4(identifier: string): string {
        this.multicastCalls.push(identifier);
        return `ipv4:${identifier}`;
      }

      getIpMac(identifier: string) {
        this.ipMacCalls.push(identifier);
        return {
          mac: "00:11:22:33:44:55",
          ipV4: ["192.168.1.20"],
          ipV6: ["fe80::1234"],
        };
      }
    };
  }

  it("resolves numeric identifiers before delegating instance and static lookups", () => {
    const FakeNetwork = makeNetworkClass();
    installWindowsMdnsScopeIdWorkaround({
      platform: "win32",
      getNetworkInterfaces: () => INTERFACES,
      networkClass: FakeNetwork,
    });

    const network = new FakeNetwork();
    expect(network.getIpMac("5")).toEqual({
      mac: "00:11:22:33:44:55",
      ipV4: ["192.168.1.20"],
      ipV6: ["fe80::1234"],
    });
    expect(FakeNetwork.getNetInterfaceZoneIpv6("5")).toBe("zone:Wi-Fi");
    expect(FakeNetwork.getMulticastInterfaceIpv4("5")).toBe("ipv4:Wi-Fi");
    expect(network.ipMacCalls).toEqual(["Wi-Fi"]);
    expect(FakeNetwork.zoneCalls).toEqual(["Wi-Fi"]);
    expect(FakeNetwork.multicastCalls).toEqual(["Wi-Fi"]);
  });

  it("is idempotent for the same network class", () => {
    const FakeNetwork = makeNetworkClass();
    const options = {
      platform: "win32" as const,
      getNetworkInterfaces: () => INTERFACES,
      networkClass: FakeNetwork,
    };
    installWindowsMdnsScopeIdWorkaround(options);
    installWindowsMdnsScopeIdWorkaround(options);

    const network = new FakeNetwork();
    network.getIpMac("5");
    expect(network.ipMacCalls).toEqual(["Wi-Fi"]);
  });

  it("does not patch network lookups outside Windows", () => {
    const FakeNetwork = makeNetworkClass();
    installWindowsMdnsScopeIdWorkaround({
      platform: "linux",
      getNetworkInterfaces: () => INTERFACES,
      networkClass: FakeNetwork,
    });

    const network = new FakeNetwork();
    network.getIpMac("5");
    expect(network.ipMacCalls).toEqual(["5"]);
    expect(FakeNetwork.getNetInterfaceZoneIpv6("5")).toBe("zone:5");
  });
});
