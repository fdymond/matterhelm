import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { afterAll, afterEach, describe, expect, it, vi } from "vitest";

const inheritedEndpointValidation = vi.hoisted(() => {
  const original = process.env.MATTER_ENDPOINT_VALIDATION;
  // Runs before the real @matter/main import below, matching an inherited
  // machine/user variable at sidecar startup and matter.js's one-time snapshot.
  process.env.MATTER_ENDPOINT_VALIDATION = "WARN";
  return { original };
});

import { MatterNode } from "./adapter.js";
import type { BridgedDeviceInfo, MatterNodeOptions } from "./adapter.js";

const BRIDGED_NODE = 0x13;
const SPEAKER = 0x22;
const ON_OFF_PLUG_IN_UNIT = 0x10a;
const ACCESS_CONTROL_CLUSTER = 0x1f;
const GROUPCAST_CLUSTER = 0x65;

const logger = {
  debug: vi.fn(),
  info: vi.fn(),
  warn: vi.fn(),
  error: vi.fn(),
  fatal: vi.fn(),
};

const activeNodes = new Set<MatterNode>();
const temporaryDirectories: string[] = [];

function restoreEndpointValidationEnvironment(): void {
  if (inheritedEndpointValidation.original === undefined) {
    delete process.env.MATTER_ENDPOINT_VALIDATION;
  } else {
    process.env.MATTER_ENDPOINT_VALIDATION = inheritedEndpointValidation.original;
  }
}

async function temporaryStorage(): Promise<string> {
  const directory = await mkdtemp(join(tmpdir(), "matterhelm-018-"));
  temporaryDirectories.push(directory);
  return directory;
}

async function createNode(storageDir: string): Promise<MatterNode> {
  const options: MatterNodeOptions = {
    id: "matterhelm-test",
    storageDir,
    vendorId: 0xfff1,
    productId: 0x8000,
    vendorName: "MatterHelm Tests",
    productName: "MatterHelm Test Bridge",
    serialNumber: "matterhelm-test-bridge",
    uniqueId: "matterhelm-test-unique",
    logger,
  };
  const node = await MatterNode.create(options);
  activeNodes.add(node);
  return node;
}

async function closeNode(node: MatterNode): Promise<void> {
  await node.close();
  activeNodes.delete(node);
}

function endpointInfo(id: string): BridgedDeviceInfo {
  return {
    id,
    name: `Test ${id}`,
    serialNumber: `serial-${id}`,
    uniqueId: `unique-${id}`,
  };
}

afterEach(async () => {
  for (const node of activeNodes) {
    await node.close();
  }
  activeNodes.clear();
  for (const directory of temporaryDirectories.splice(0)) {
    await rm(directory, { recursive: true, force: true });
  }
  vi.clearAllMocks();
});

afterAll(() => {
  restoreEndpointValidationEnvironment();
});

describe("matter.js 0.18 adapter integration", { concurrent: false }, () => {
  it("replaces an invalid import-time endpoint-validation snapshot before node creation", async () => {
    expect(process.env.MATTER_ENDPOINT_VALIDATION).toBe("WARN");
    try {
      const node = await createNode(await temporaryStorage());
      expect(node.endpointValidationMode).toBe("warn");
      expect(logger.warn).toHaveBeenCalledWith(
        {
          evt: "matter.endpoint-validation.invalid",
          value: "WARN",
          fallback: "warn",
        },
        "invalid MATTER_ENDPOINT_VALIDATION; forcing warn mode",
      );
      await closeNode(node);
    } finally {
      restoreEndpointValidationEnvironment();
    }
  });

  it("treats an inherited empty endpoint-validation value as invalid and uses warn", async () => {
    process.env.MATTER_ENDPOINT_VALIDATION = "";
    try {
      const node = await createNode(await temporaryStorage());
      expect(node.endpointValidationMode).toBe("warn");
      expect(process.env.MATTER_ENDPOINT_VALIDATION).toBe("");
      expect(logger.warn).toHaveBeenCalledWith(
        {
          evt: "matter.endpoint-validation.invalid",
          value: "",
          fallback: "warn",
        },
        "invalid MATTER_ENDPOINT_VALIDATION; forcing warn mode",
      );
      await closeNode(node);
    } finally {
      restoreEndpointValidationEnvironment();
    }
  });

  it("constructs every endpoint under strict validation with exact Speaker limits", async () => {
    expect(process.env.MATTER_ENDPOINT_VALIDATION).toBe("strict");
    const node = await createNode(await temporaryStorage());
    expect(node.endpointValidationMode).toBe("strict");
    const speaker = await node.addSpeaker(endpointInfo("speaker"));
    const plugs = await Promise.all(
      ["playpause", "next", "previous", "power", "custom-movie-mode"].map((id) =>
        node.addPlug(endpointInfo(id)),
      ),
    );

    expect(speaker.levelLimits).toEqual({ min: 0, max: 254 });
    expect(await node.synchronizeEndpointConfiguration()).toMatchObject({ changed: false });
    await closeNode(node);

    expect(logger.warn).not.toHaveBeenCalledWith(
      expect.objectContaining({ evt: "matter.log" }),
      expect.stringContaining("device type"),
    );
    expect(plugs).toHaveLength(5);
  });

  it("keeps every bridged endpoint single-purpose with Groupcast and AuxiliaryAcl root-only", async () => {
    const node = await createNode(await temporaryStorage());
    const speaker = await node.addSpeaker(endpointInfo("speaker"));
    const plugs = await Promise.all(
      ["playpause", "next", "previous", "power", "custom-movie-mode"].map((id) =>
        node.addPlug(endpointInfo(id)),
      ),
    );

    expect(speaker.deviceTypes).toEqual([BRIDGED_NODE, SPEAKER]);
    for (const plug of plugs) {
      expect(plug.deviceTypes).toEqual([BRIDGED_NODE, ON_OFF_PLUG_IN_UNIT]);
    }
    for (const endpoint of [speaker, ...plugs]) {
      expect(endpoint.serverClusters).not.toContain(ACCESS_CONTROL_CLUSTER);
      expect(endpoint.serverClusters).not.toContain(GROUPCAST_CLUSTER);
    }
    expect(node.aggregatorServerClusters).not.toContain(ACCESS_CONTROL_CLUSTER);
    expect(node.aggregatorServerClusters).not.toContain(GROUPCAST_CLUSTER);
    expect(node.rootServerClusters).toContain(ACCESS_CONTROL_CLUSTER);
    expect(node.rootServerClusters).toContain(GROUPCAST_CLUSTER);
    expect(node.hasRootAuxiliaryAcl).toBe(true);
    await closeNode(node);
  });

  it("preserves endpoint numbers across restart, absence, and later re-addition", async () => {
    const storage = await temporaryStorage();

    const first = await createNode(storage);
    const firstSpeaker = await first.addSpeaker(endpointInfo("speaker"));
    const firstCustom = await first.addPlug(endpointInfo("custom-movie-mode"));
    const expectedNumbers = {
      speaker: firstSpeaker.endpointNumber,
      custom: firstCustom.endpointNumber,
    };
    await first.synchronizeEndpointConfiguration();
    await closeNode(first);

    const absent = await createNode(storage);
    const retainedSpeaker = await absent.addSpeaker(endpointInfo("speaker"));
    const replacementCustom = await absent.addPlug(endpointInfo("custom-other"));
    expect(retainedSpeaker.endpointNumber).toBe(expectedNumbers.speaker);
    expect(replacementCustom.endpointNumber).not.toBe(expectedNumbers.custom);
    await absent.synchronizeEndpointConfiguration();
    await closeNode(absent);

    const restored = await createNode(storage);
    const restoredSpeaker = await restored.addSpeaker(endpointInfo("speaker"));
    const restoredCustom = await restored.addPlug(endpointInfo("custom-movie-mode"));
    expect(restoredSpeaker.endpointNumber).toBe(expectedNumbers.speaker);
    expect(restoredCustom.endpointNumber).toBe(expectedNumbers.custom);
    await restored.synchronizeEndpointConfiguration();
    await closeNode(restored);
  });

  it("never calls the 0.18-throwing parts.delete or parts.clear APIs", async () => {
    const source = await readFile(new URL("./adapter.ts", import.meta.url), "utf8");
    expect(source).not.toMatch(/\.parts\.(?:delete|clear)\s*\(/u);
  });

  it("increments ConfigurationVersion exactly once for each changed endpoint set", async () => {
    const storage = await temporaryStorage();

    const baseline = await createNode(storage);
    await baseline.addSpeaker(endpointInfo("speaker"));
    await baseline.addPlug(endpointInfo("custom-movie-mode"));
    expect(await baseline.synchronizeEndpointConfiguration()).toEqual({
      changed: false,
      configurationVersion: 1,
    });
    await closeNode(baseline);

    const unchanged = await createNode(storage);
    await unchanged.addSpeaker(endpointInfo("speaker"));
    await unchanged.addPlug(endpointInfo("custom-movie-mode"));
    expect(await unchanged.synchronizeEndpointConfiguration()).toEqual({
      changed: false,
      configurationVersion: 1,
    });
    await closeNode(unchanged);

    const changed = await createNode(storage);
    await changed.addSpeaker(endpointInfo("speaker"));
    expect(await changed.synchronizeEndpointConfiguration()).toEqual({
      changed: true,
      configurationVersion: 2,
    });
    expect(await changed.synchronizeEndpointConfiguration()).toEqual({
      changed: false,
      configurationVersion: 2,
    });
    await closeNode(changed);

    const stableAgain = await createNode(storage);
    await stableAgain.addSpeaker(endpointInfo("speaker"));
    expect(await stableAgain.synchronizeEndpointConfiguration()).toEqual({
      changed: false,
      configurationVersion: 2,
    });
    await closeNode(stableAgain);

    const typeChanged = await createNode(storage);
    await typeChanged.addPlug(endpointInfo("speaker"));
    expect(await typeChanged.synchronizeEndpointConfiguration()).toEqual({
      changed: true,
      configurationVersion: 3,
    });
    await closeNode(typeChanged);
  });
});
