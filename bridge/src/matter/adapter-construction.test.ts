import { beforeEach, describe, expect, it, vi } from "vitest";

const seam = vi.hoisted(() => ({
  addAttempts: 0,
  closeCalls: 0,
  failEndpointAdd: false,
  failServerAdd: true,
  invokePlugOn: (): Promise<void> => Promise.resolve(),
}));

interface CommandServer {
  on(): unknown;
}

type CommandServerConstructor = new () => CommandServer;

function isCommandServerConstructor(value: unknown): value is CommandServerConstructor {
  return typeof value === "function";
}

vi.mock("@matter/main", () => {
  class FakeEndpoint {
    add(): Promise<void> {
      return seam.failEndpointAdd
        ? Promise.reject(new Error("injected plug add failure"))
        : Promise.resolve();
    }
  }
  return {
    Endpoint: FakeEndpoint,
    Environment: { default: { vars: { set: () => undefined } } },
    LogDestination: (value: unknown) => value,
    LogFormat: () => () => "",
    Logger: { destinations: { default: {} }, facilityLevels: {}, level: "notice" },
    MaybePromise: {
      then: (value: unknown, callback: () => unknown) => Promise.resolve(value).then(callback),
    },
    ServerNode: {
      create: () =>
        Promise.resolve({
          add: () => {
            seam.addAttempts += 1;
            if (seam.failServerAdd) {
              seam.failServerAdd = false;
              return Promise.reject(new Error("injected aggregator add failure"));
            }
            return Promise.resolve();
          },
          close: () => {
            seam.closeCalls += 1;
            return Promise.resolve();
          },
        }),
    },
    VendorId: (value: number) => value,
  };
});

vi.mock("@matter/main/behaviors/bridged-device-basic-information", () => ({
  BridgedDeviceBasicInformationServer: { testSeam: true },
}));

vi.mock("@matter/main/devices/on-off-plug-in-unit", () => ({
  OnOffPlugInUnitDevice: {
    with: (_information: unknown, commandServer: unknown) => {
      seam.invokePlugOn = async () => {
        if (!isCommandServerConstructor(commandServer)) {
          throw new Error("expected command observer server class");
        }
        const instance = new commandServer();
        await Promise.resolve(instance.on());
      };
      return { testSeam: true };
    },
  },
  OnOffPlugInUnitRequirements: {
    OnOffServer: class {
      readonly endpoint = { id: "failed-plug" };
      on(): void {
        return undefined;
      }
      off(): void {
        return undefined;
      }
    },
  },
}));

vi.mock("@matter/main/devices/speaker", () => ({
  SpeakerDevice: { with: () => ({ testSeam: true }) },
}));

vi.mock("@matter/main/endpoints/aggregator", () => ({
  AggregatorEndpoint: { deviceType: {} },
}));

vi.mock("@matter/nodejs", () => ({
  NodeJsNetwork: class {
    static getMulticastInterfaceIpv4(): undefined {
      return undefined;
    }
    static getNetInterfaceZoneIpv6(): undefined {
      return undefined;
    }
    getIpMac(): undefined {
      return undefined;
    }
  },
}));

import { MatterNode } from "./adapter.js";

const options = {
  id: "test-node",
  storageDir: "test-storage",
  vendorId: 0xfff1,
  productId: 0x8000,
  vendorName: "Test Vendor",
  productName: "Test Bridge",
  serialNumber: "test-serial",
  uniqueId: "test-unique",
};

describe("MatterNode construction cleanup", () => {
  beforeEach(() => {
    seam.addAttempts = 0;
    seam.closeCalls = 0;
    seam.failEndpointAdd = false;
    seam.failServerAdd = true;
  });

  it("closes the ServerNode when adding the aggregator fails and permits retry", async () => {
    await expect(MatterNode.create(options)).rejects.toThrow("injected aggregator add failure");
    expect(seam.closeCalls).toBe(1);

    const retry = await MatterNode.create(options);
    expect(seam.addAttempts).toBe(2);
    await retry.close();
    expect(seam.closeCalls).toBe(2);
  });

  it("does not retain a plug observer when adding its endpoint fails", async () => {
    seam.failServerAdd = false;
    const node = await MatterNode.create(options);
    seam.failEndpointAdd = true;
    let observed = 0;

    await expect(
      node.addPlug(
        {
          id: "failed-plug",
          name: "Failed Plug",
          serialNumber: "failed-plug-serial",
          uniqueId: "failed-plug-unique",
        },
        () => {
          observed += 1;
        },
      ),
    ).rejects.toThrow("injected plug add failure");
    await seam.invokePlugOn();

    expect(observed).toBe(0);
    await node.close();
  });
});
