import { beforeEach, describe, expect, it, vi } from "vitest";

const seam = vi.hoisted(
  (): {
    addAttempts: number;
    closeCalls: number;
    failEndpointAdd: boolean;
    failServerAdd: boolean;
    commissioned: boolean;
    configurationVersion: number;
    endpointStates: {
      descriptor: {
        deviceTypeList: { deviceType: number; revision: number }[];
        serverList: number[];
      };
    }[];
    environmentVariables: Map<string, unknown>;
    increaseConfigurationVersionCalls: number;
    invokePlugOn: () => Promise<void>;
    logDestination: unknown;
    loggerDestinations: Record<string, unknown>;
    matterFormats: string[];
    storage: Map<string, unknown>;
  } => ({
    addAttempts: 0,
    closeCalls: 0,
    failEndpointAdd: false,
    failServerAdd: true,
    commissioned: false,
    configurationVersion: 1,
    endpointStates: [],
    environmentVariables: new Map<string, unknown>(),
    increaseConfigurationVersionCalls: 0,
    invokePlugOn: (): Promise<void> => Promise.resolve(),
    logDestination: undefined,
    loggerDestinations: { default: {} },
    matterFormats: [],
    storage: new Map<string, unknown>(),
  }),
);

interface CommandServer {
  on(): unknown;
}

type CommandServerConstructor = new () => CommandServer;

function isCommandServerConstructor(value: unknown): value is CommandServerConstructor {
  return typeof value === "function";
}

vi.mock("@matter/main", () => {
  const FakeLogFormat = Object.assign(() => (values: unknown) => String(values), {
    PLAIN: "plain",
  });
  class FakeEndpoint {
    readonly state = {
      descriptor: {
        deviceTypeList: [{ deviceType: 0x000e, revision: 1 }],
        serverList: [0x001d],
      },
    };

    constructor() {
      seam.endpointStates.push(this.state);
    }

    add(): Promise<void> {
      return seam.failEndpointAdd
        ? Promise.reject(new Error("injected plug add failure"))
        : Promise.resolve();
    }
  }
  return {
    Endpoint: FakeEndpoint,
    Environment: {
      default: {
        vars: {
          set: (key: string, value: unknown) => seam.environmentVariables.set(key, value),
        },
      },
    },
    LogDestination: (value: unknown) => {
      seam.logDestination = value;
      return value;
    },
    LogFormat: FakeLogFormat,
    Logger: {
      destinations: seam.loggerDestinations,
      facilityLevels: {},
      level: "notice",
      set format(value: string) {
        seam.matterFormats.push(value);
      },
    },
    MaybePromise: {
      then: (value: unknown, callback: () => unknown) => Promise.resolve(value).then(callback),
    },
    ServerNode: {
      create: () =>
        Promise.resolve({
          act: (callback: (agent: { get: () => unknown }) => unknown) =>
            Promise.resolve(
              callback({
                get: () => ({
                  increaseConfigurationVersion: () => {
                    seam.increaseConfigurationVersionCalls += 1;
                    seam.configurationVersion += 1;
                    return Promise.resolve();
                  },
                }),
              }),
            ),
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
          events: {
            sessions: {
              closed: { on: () => undefined },
              opened: { on: () => undefined },
              subscriptionAdded: { on: () => undefined },
              subscriptionsChanged: { on: () => undefined },
            },
          },
          env: {
            get: () => ({
              createContext: () => ({
                get: (key: string) => Promise.resolve(seam.storage.get(key)),
                has: (key: string) => Promise.resolve(seam.storage.has(key)),
                set: (key: string, value: unknown) => {
                  seam.storage.set(key, value);
                  return Promise.resolve();
                },
              }),
            }),
          },
          lifecycle: {
            get isCommissioned() {
              return seam.commissioned;
            },
          },
          state: {
            basicInformation: {
              get configurationVersion() {
                return seam.configurationVersion;
              },
            },
            descriptor: {
              deviceTypeList: [{ deviceType: 0x000e, revision: 1 }],
              serverList: [0x001d, 0x0028],
            },
          },
        }),
    },
    StorageManager: { testSeam: true },
    VendorId: (value: number) => value,
  };
});

vi.mock("@matter/main/behaviors/basic-information", () => ({
  BasicInformationServer: { testSeam: true },
}));

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
  // Deliberately lacks every ADR-010 symbol: diagnostic disable must skip
  // compatibility assertion and patch installation entirely.
  NodeJsNetwork: function NodeJsNetwork(): undefined {
    return undefined;
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
  disableMdnsScopeIdWorkaround: true,
};

describe("MatterNode construction cleanup", () => {
  beforeEach(() => {
    process.env.MATTER_ENDPOINT_VALIDATION = "strict";
    seam.addAttempts = 0;
    seam.closeCalls = 0;
    seam.failEndpointAdd = false;
    seam.failServerAdd = true;
    seam.commissioned = false;
    seam.configurationVersion = 1;
    seam.endpointStates.length = 0;
    seam.environmentVariables.clear();
    seam.increaseConfigurationVersionCalls = 0;
    seam.logDestination = undefined;
    delete seam.loggerDestinations.pino;
    seam.loggerDestinations.default = {};
    seam.matterFormats.length = 0;
    seam.storage.clear();
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

  it("keeps stdout pino-only and skips incompatible ADR-010 symbols only when diagnostically disabled", async () => {
    seam.failServerAdd = false;
    const logger = {
      debug: vi.fn(),
      info: vi.fn(),
      warn: vi.fn(),
      error: vi.fn(),
      fatal: vi.fn(),
    };

    const node = await MatterNode.create({
      ...options,
      logger,
      disableMdnsScopeIdWorkaround: true,
    });

    expect(seam.matterFormats).toEqual(["plain"]);
    expect(seam.loggerDestinations).not.toHaveProperty("default");
    expect(seam.loggerDestinations).toHaveProperty("pino", seam.logDestination);
    expect(logger.warn).toHaveBeenCalledWith(
      { evt: "matter.mdns-scopeid-workaround.disabled" },
      "ADR-010 mDNS scope-id workaround disabled for diagnostics only",
    );
    await node.close();
  });

  it.each(["", "inherited-invalid-value"])(
    "warns and forces warn validation when the inherited mode is %j",
    async (inheritedValue) => {
      seam.failServerAdd = false;
      process.env.MATTER_ENDPOINT_VALIDATION = inheritedValue;
      const logger = {
        debug: vi.fn(),
        info: vi.fn(),
        warn: vi.fn(),
        error: vi.fn(),
        fatal: vi.fn(),
      };

      const node = await MatterNode.create({ ...options, logger });

      expect(node.endpointValidationMode).toBe("warn");
      expect(process.env.MATTER_ENDPOINT_VALIDATION).toBe(inheritedValue);
      expect(seam.environmentVariables.get("endpoint.validation")).toBe("warn");
      expect(logger.warn).toHaveBeenCalledWith(
        {
          evt: "matter.endpoint-validation.invalid",
          value: inheritedValue,
          fallback: "warn",
        },
        "invalid MATTER_ENDPOINT_VALIDATION; forcing warn mode",
      );
      await node.close();
    },
  );

  it("increments once when a commissioned node has no stored composition fingerprint", async () => {
    seam.failServerAdd = false;
    seam.commissioned = true;
    const node = await MatterNode.create(options);

    expect(await node.synchronizeEndpointConfiguration()).toEqual({
      changed: true,
      configurationVersion: 2,
    });
    expect(await node.synchronizeEndpointConfiguration()).toEqual({
      changed: false,
      configurationVersion: 2,
    });
    expect(seam.increaseConfigurationVersionCalls).toBe(1);
    await node.close();
  });

  it("stores an uncommissioned first-run baseline without incrementing", async () => {
    seam.failServerAdd = false;
    const node = await MatterNode.create(options);

    expect(await node.synchronizeEndpointConfiguration()).toEqual({
      changed: false,
      configurationVersion: 1,
    });
    expect(seam.increaseConfigurationVersionCalls).toBe(0);
    await node.close();
  });

  it("increments for a server-cluster-list change and not for an unchanged fingerprint", async () => {
    seam.failServerAdd = false;
    const node = await MatterNode.create(options);
    await node.addPlug({
      id: "cluster-test",
      name: "Cluster Test",
      serialNumber: "cluster-test-serial",
      uniqueId: "cluster-test-unique",
    });
    expect(await node.synchronizeEndpointConfiguration()).toMatchObject({ changed: false });

    const publishedEndpoint = seam.endpointStates.at(-1);
    if (publishedEndpoint === undefined) {
      throw new Error("expected a published endpoint state");
    }
    publishedEndpoint.descriptor.serverList.push(0x0065);

    expect(await node.synchronizeEndpointConfiguration()).toEqual({
      changed: true,
      configurationVersion: 2,
    });
    expect(await node.synchronizeEndpointConfiguration()).toEqual({
      changed: false,
      configurationVersion: 2,
    });
    expect(seam.increaseConfigurationVersionCalls).toBe(1);
    await node.close();
  });
});
