import { afterEach, describe, expect, it, vi } from "vitest";

import { loadConfig, parseConfig } from "./config.js";

const TOKEN = "session-token-abc123";
const APPDATA = "C:\\Users\\test\\AppData\\Roaming";

/** Minimal valid env: the required token plus a fixed APPDATA so the
 * storage-dir default never hits the "no APPDATA" fatal branch by accident. */
function baseEnv(
  overrides: Record<string, string | undefined> = {},
): Record<string, string | undefined> {
  return { HTPC_BRIDGE_IPC_TOKEN: TOKEN, APPDATA, ...overrides };
}

describe("parseConfig", () => {
  it("applies every BLUEPRINT §2.3 default when only the token/APPDATA are set", () => {
    const config = parseConfig(baseEnv());
    expect(config).toEqual({
      ipcPort: 39531,
      ipcToken: TOKEN,
      storageDir: `${APPDATA}\\HtpcMatterBridge\\matter`,
      logLevel: "info",
      deviceNames: {
        speaker: "HTPC Speaker",
        playPause: "HTPC Play Pause",
        next: "HTPC Next",
        previous: "HTPC Previous",
        power: "HTPC Power",
      },
    });
    expect(config.mdnsInterface).toBeUndefined();
    expect(config.matterPort).toBeUndefined();
  });

  it("has no own mdnsInterface/matterPort keys when unset (exactOptionalPropertyTypes contract)", () => {
    const config = parseConfig(baseEnv());
    expect(Object.hasOwn(config, "mdnsInterface")).toBe(false);
    expect(Object.hasOwn(config, "matterPort")).toBe(false);
  });

  describe("HTPC_BRIDGE_IPC_TOKEN", () => {
    it("is fatal when missing", () => {
      expect(() => {
        parseConfig({});
      }).toThrow(/HTPC_BRIDGE_IPC_TOKEN/);
    });

    it("is fatal when empty", () => {
      expect(() => {
        parseConfig({ HTPC_BRIDGE_IPC_TOKEN: "" });
      }).toThrow(/HTPC_BRIDGE_IPC_TOKEN/);
    });

    it("is never embedded in an error raised for a different field", () => {
      let message = "";
      try {
        parseConfig(baseEnv({ HTPC_BRIDGE_IPC_PORT: "not-a-port" }));
      } catch (err) {
        message = err instanceof Error ? err.message : String(err);
      }
      expect(message).toContain("HTPC_BRIDGE_IPC_PORT");
      expect(message).not.toContain(TOKEN);
    });
  });

  describe("HTPC_BRIDGE_IPC_PORT", () => {
    it("accepts the range boundaries", () => {
      expect(parseConfig(baseEnv({ HTPC_BRIDGE_IPC_PORT: "1024" })).ipcPort).toBe(1024);
      expect(parseConfig(baseEnv({ HTPC_BRIDGE_IPC_PORT: "65535" })).ipcPort).toBe(65535);
    });

    it("rejects a non-integer value", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_IPC_PORT: "39531.5" }));
      }).toThrow(/HTPC_BRIDGE_IPC_PORT/);
    });

    it("rejects a value below 1024", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_IPC_PORT: "1023" }));
      }).toThrow(/HTPC_BRIDGE_IPC_PORT/);
    });

    it("rejects a value above 65535", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_IPC_PORT: "65536" }));
      }).toThrow(/HTPC_BRIDGE_IPC_PORT/);
    });

    it("rejects a non-numeric string", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_IPC_PORT: "abc" }));
      }).toThrow(/HTPC_BRIDGE_IPC_PORT/);
    });
  });

  describe("HTPC_BRIDGE_MATTER_PORT", () => {
    it("is undefined when unset", () => {
      expect(parseConfig(baseEnv()).matterPort).toBeUndefined();
    });

    it("is threaded through when set to a valid port", () => {
      expect(parseConfig(baseEnv({ HTPC_BRIDGE_MATTER_PORT: "5542" })).matterPort).toBe(5542);
    });

    it("rejects an out-of-range value", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_MATTER_PORT: "70000" }));
      }).toThrow(/HTPC_BRIDGE_MATTER_PORT/);
    });
  });

  describe("HTPC_BRIDGE_STORAGE_DIR", () => {
    it("uses the explicit value when set", () => {
      const config = parseConfig(baseEnv({ HTPC_BRIDGE_STORAGE_DIR: "C:\\scratch\\matter" }));
      expect(config.storageDir).toBe("C:\\scratch\\matter");
    });

    it("derives %APPDATA%\\HtpcMatterBridge\\matter when unset", () => {
      const config = parseConfig(baseEnv({ APPDATA: "D:\\CustomAppData" }));
      expect(config.storageDir).toBe("D:\\CustomAppData\\HtpcMatterBridge\\matter");
    });

    it("is fatal when both the override and APPDATA are unavailable", () => {
      expect(() => {
        parseConfig(baseEnv({ APPDATA: undefined }));
      }).toThrow(/HTPC_BRIDGE_STORAGE_DIR/);
    });
  });

  describe("HTPC_BRIDGE_LOG_LEVEL", () => {
    it.each(["fatal", "error", "warn", "info", "debug", "trace", "silent"])(
      "accepts pino level %s",
      (level) => {
        expect(parseConfig(baseEnv({ HTPC_BRIDGE_LOG_LEVEL: level })).logLevel).toBe(level);
      },
    );

    it("rejects an unknown level", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_LOG_LEVEL: "verbose" }));
      }).toThrow(/HTPC_BRIDGE_LOG_LEVEL/);
    });
  });

  describe("HTPC_BRIDGE_DEVICE_NAMES", () => {
    it("overrides only the given fields, defaulting the rest", () => {
      const config = parseConfig(
        baseEnv({ HTPC_BRIDGE_DEVICE_NAMES: JSON.stringify({ speaker: "Living Room Speaker" }) }),
      );
      expect(config.deviceNames).toEqual({
        speaker: "Living Room Speaker",
        playPause: "HTPC Play Pause",
        next: "HTPC Next",
        previous: "HTPC Previous",
        power: "HTPC Power",
      });
    });

    it("accepts a full override of every field", () => {
      const names = { speaker: "S", playPause: "P", next: "N", previous: "V", power: "W" };
      const config = parseConfig(baseEnv({ HTPC_BRIDGE_DEVICE_NAMES: JSON.stringify(names) }));
      expect(config.deviceNames).toEqual(names);
    });

    it("defaults speaker specifically when every other field is overridden", () => {
      const config = parseConfig(
        baseEnv({
          HTPC_BRIDGE_DEVICE_NAMES: JSON.stringify({
            playPause: "P",
            next: "N",
            previous: "V",
            power: "W",
          }),
        }),
      );
      expect(config.deviceNames.speaker).toBe("HTPC Speaker");
    });

    it("is fatal (not silent) on malformed JSON", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_DEVICE_NAMES: "{not json" }));
      }).toThrow(/HTPC_BRIDGE_DEVICE_NAMES/);
    });

    it("rejects an unknown key", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_DEVICE_NAMES: JSON.stringify({ bogus: "x" }) }));
      }).toThrow(/HTPC_BRIDGE_DEVICE_NAMES/);
    });

    it("rejects a non-string value", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_DEVICE_NAMES: JSON.stringify({ speaker: 5 }) }));
      }).toThrow(/HTPC_BRIDGE_DEVICE_NAMES/);
    });

    it("rejects an empty-string value", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_DEVICE_NAMES: JSON.stringify({ speaker: "" }) }));
      }).toThrow(/HTPC_BRIDGE_DEVICE_NAMES/);
    });

    it("rejects a JSON array (not an object)", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_DEVICE_NAMES: "[]" }));
      }).toThrow(/HTPC_BRIDGE_DEVICE_NAMES/);
    });
  });

  describe("HTPC_BRIDGE_MDNS_INTERFACE", () => {
    it("is undefined when unset", () => {
      expect(parseConfig(baseEnv()).mdnsInterface).toBeUndefined();
    });

    it("passes an explicit value through untouched", () => {
      expect(parseConfig(baseEnv({ HTPC_BRIDGE_MDNS_INTERFACE: "Ethernet" })).mdnsInterface).toBe(
        "Ethernet",
      );
    });

    it("treats an empty string as unset", () => {
      expect(
        parseConfig(baseEnv({ HTPC_BRIDGE_MDNS_INTERFACE: "" })).mdnsInterface,
      ).toBeUndefined();
    });
  });
});

describe("loadConfig", () => {
  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it("parses the live process.env", () => {
    vi.stubEnv("HTPC_BRIDGE_IPC_TOKEN", TOKEN);
    vi.stubEnv("APPDATA", APPDATA);
    vi.stubEnv("HTPC_BRIDGE_IPC_PORT", "40000");
    expect(loadConfig()).toMatchObject({ ipcPort: 40000, ipcToken: TOKEN });
  });
});
