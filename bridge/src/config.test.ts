import { join } from "node:path";

import { afterEach, describe, expect, it, vi } from "vitest";

import { defaultMatterLogLevel, loadConfig, parseConfig } from "./config.js";

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
      // join(), not a literal: CI runs this suite on ubuntu too, where the
      // separator is "/" (the product itself is Windows-only).
      storageDir: join(APPDATA, "MatterHelm", "matter"),
      logLevel: "info",
      matterLogLevel: "notice",
      matterLogFacilities: { Commissioning: "warn" },
      endpoints: {
        speaker: { name: "HTPC Speaker", enabled: true },
        playPause: { name: "HTPC Play Pause", enabled: true },
        next: { name: "HTPC Next", enabled: true },
        previous: { name: "HTPC Previous", enabled: true },
        power: { name: "HTPC Power", enabled: true },
        custom: [],
      },
      momentaryResetMs: 300,
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

    it("derives %APPDATA%\\MatterHelm\\matter when unset", () => {
      const config = parseConfig(baseEnv({ APPDATA: "D:\\CustomAppData" }));
      expect(config.storageDir).toBe(join("D:\\CustomAppData", "MatterHelm", "matter"));
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

  describe("HTPC_BRIDGE_MATTER_LOG_LEVEL (ADR-006 §1)", () => {
    it.each(["debug", "info", "notice", "warn", "error", "fatal"])(
      "accepts matter.js level %s",
      (level) => {
        expect(parseConfig(baseEnv({ HTPC_BRIDGE_MATTER_LOG_LEVEL: level })).matterLogLevel).toBe(
          level,
        );
      },
    );

    it.each([
      // pino-only names are NOT matter.js levels — accepting them silently
      // would hide that matter.js can neither trace nor go fully silent.
      "trace",
      "silent",
      "verbose",
      "NOTICE",
    ])("rejects %s as fatal", (level) => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_MATTER_LOG_LEVEL: level }));
      }).toThrow(/HTPC_BRIDGE_MATTER_LOG_LEVEL/);
    });

    it("treats an empty string like unset (derived default)", () => {
      expect(parseConfig(baseEnv({ HTPC_BRIDGE_MATTER_LOG_LEVEL: "" })).matterLogLevel).toBe(
        "notice",
      );
    });

    it.each([
      ["trace", "debug"],
      ["debug", "debug"],
      ["info", "notice"],
      ["warn", "warn"],
      ["error", "error"],
      ["fatal", "fatal"],
      ["silent", "fatal"],
    ] as const)("defaults from our logLevel %s to matter.js %s when unset", (pino, matter) => {
      expect(defaultMatterLogLevel(pino)).toBe(matter);
      expect(parseConfig(baseEnv({ HTPC_BRIDGE_LOG_LEVEL: pino })).matterLogLevel).toBe(matter);
    });
  });

  describe("HTPC_BRIDGE_MATTER_LOG_FACILITIES (ADR-006 §1; S5-R F1 default)", () => {
    it("defaults to suppressing the Commissioning facility below warn (passcode leak, S5-R F1)", () => {
      expect(parseConfig(baseEnv()).matterLogFacilities).toEqual({ Commissioning: "warn" });
    });

    it("treats an empty string as unset (default still applies)", () => {
      expect(
        parseConfig(baseEnv({ HTPC_BRIDGE_MATTER_LOG_FACILITIES: "" })).matterLogFacilities,
      ).toEqual({ Commissioning: "warn" });
    });

    it("merges a facility->level map over the default", () => {
      const facilities = { MdnsServer: "debug", SessionManager: "info" };
      const config = parseConfig(
        baseEnv({ HTPC_BRIDGE_MATTER_LOG_FACILITIES: JSON.stringify(facilities) }),
      );
      expect(config.matterLogFacilities).toEqual({ Commissioning: "warn", ...facilities });
    });

    it("lets an explicit Commissioning override win over the default (pairing-spike escape hatch)", () => {
      const config = parseConfig(
        baseEnv({ HTPC_BRIDGE_MATTER_LOG_FACILITIES: '{"Commissioning":"notice"}' }),
      );
      expect(config.matterLogFacilities).toEqual({ Commissioning: "notice" });
    });

    it("accepts an empty object (default only)", () => {
      expect(
        parseConfig(baseEnv({ HTPC_BRIDGE_MATTER_LOG_FACILITIES: "{}" })).matterLogFacilities,
      ).toEqual({ Commissioning: "warn" });
    });

    it("is fatal (not silent) on malformed JSON", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_MATTER_LOG_FACILITIES: "{not json" }));
      }).toThrow(/HTPC_BRIDGE_MATTER_LOG_FACILITIES/);
    });

    it.each([
      ["a JSON array", "[]"],
      ["a JSON string", '"debug"'],
      ["a JSON number", "5"],
      ["JSON null", "null"],
    ])("rejects %s (not an object)", (_desc, raw) => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_MATTER_LOG_FACILITIES: raw }));
      }).toThrow(/HTPC_BRIDGE_MATTER_LOG_FACILITIES/);
    });

    it("rejects an unknown level value", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_MATTER_LOG_FACILITIES: '{"MdnsServer":"verbose"}' }));
      }).toThrow(/HTPC_BRIDGE_MATTER_LOG_FACILITIES/);
    });

    it("rejects a pino-only level value (trace)", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_MATTER_LOG_FACILITIES: '{"MdnsServer":"trace"}' }));
      }).toThrow(/HTPC_BRIDGE_MATTER_LOG_FACILITIES/);
    });

    it("rejects a non-string level value", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_MATTER_LOG_FACILITIES: '{"MdnsServer":0}' }));
      }).toThrow(/HTPC_BRIDGE_MATTER_LOG_FACILITIES/);
    });

    it("rejects an empty facility name", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_MATTER_LOG_FACILITIES: '{"":"debug"}' }));
      }).toThrow(/HTPC_BRIDGE_MATTER_LOG_FACILITIES/);
    });
  });

  describe("HTPC_BRIDGE_ENDPOINTS (ADR-004 §2)", () => {
    /** The full shape the tray app sends (ADR-004's worked example). */
    const fullShape = {
      speaker: { name: "HTPC Speaker", enabled: true },
      playPause: { name: "HTPC Play Pause", enabled: false },
      next: { name: "HTPC Next", enabled: true },
      previous: { name: "HTPC Previous", enabled: true },
      power: { name: "HTPC Power", enabled: true },
      custom: [{ key: "movie-mode", name: "Movie Mode" }],
    };

    const withEndpoints = (value: unknown): Record<string, string | undefined> =>
      baseEnv({ HTPC_BRIDGE_ENDPOINTS: JSON.stringify(value) });

    it("parses the full ADR-004 shape verbatim", () => {
      expect(parseConfig(withEndpoints(fullShape)).endpoints).toEqual(fullShape);
    });

    it("treats an empty string like unset (all defaults)", () => {
      const config = parseConfig(baseEnv({ HTPC_BRIDGE_ENDPOINTS: "" }));
      expect(config.endpoints).toEqual(parseConfig(baseEnv()).endpoints);
    });

    it("carries a disabled built-in through as enabled:false (bridge omits it)", () => {
      expect(parseConfig(withEndpoints(fullShape)).endpoints.playPause.enabled).toBe(false);
    });

    it("defaults an omitted built-in to enabled with its HTPC name", () => {
      const config = parseConfig(
        withEndpoints({ speaker: { name: "Living Room", enabled: true } }),
      );
      expect(config.endpoints.speaker).toEqual({ name: "Living Room", enabled: true });
      expect(config.endpoints.power).toEqual({ name: "HTPC Power", enabled: true });
      expect(config.endpoints.custom).toEqual([]);
    });

    it("defaults an omitted name/enabled field inside a built-in entry", () => {
      const config = parseConfig(
        withEndpoints({ next: { enabled: false }, power: { name: "TV" } }),
      );
      expect(config.endpoints.next).toEqual({ name: "HTPC Next", enabled: false });
      expect(config.endpoints.power).toEqual({ name: "TV", enabled: true });
    });

    it("preserves the order of custom commands", () => {
      const custom = [
        { key: "b-second", name: "B" },
        { key: "a-first", name: "A" },
      ];
      expect(parseConfig(withEndpoints({ custom })).endpoints.custom).toEqual(custom);
    });

    it("is fatal (not silent) on malformed JSON", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_ENDPOINTS: "{not json" }));
      }).toThrow(/HTPC_BRIDGE_ENDPOINTS/);
    });

    it("rejects a JSON array (not an object)", () => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_ENDPOINTS: "[]" }));
      }).toThrow(/HTPC_BRIDGE_ENDPOINTS/);
    });

    it("rejects an unknown top-level key", () => {
      expect(() => {
        parseConfig(withEndpoints({ bogus: { name: "x", enabled: true } }));
      }).toThrow(/HTPC_BRIDGE_ENDPOINTS/);
    });

    it("rejects an unknown key inside a built-in entry", () => {
      expect(() => {
        parseConfig(withEndpoints({ speaker: { name: "S", enabled: true, extra: 1 } }));
      }).toThrow(/HTPC_BRIDGE_ENDPOINTS/);
    });

    it("rejects an empty built-in name", () => {
      expect(() => {
        parseConfig(withEndpoints({ speaker: { name: "", enabled: true } }));
      }).toThrow(/HTPC_BRIDGE_ENDPOINTS/);
    });

    it("rejects a non-boolean enabled flag", () => {
      expect(() => {
        parseConfig(withEndpoints({ speaker: { name: "S", enabled: "yes" } }));
      }).toThrow(/HTPC_BRIDGE_ENDPOINTS/);
    });

    it("rejects a non-array custom field", () => {
      expect(() => {
        parseConfig(withEndpoints({ custom: { key: "movie-mode", name: "M" } }));
      }).toThrow(/HTPC_BRIDGE_ENDPOINTS/);
    });

    it("rejects a custom entry missing its key", () => {
      expect(() => {
        parseConfig(withEndpoints({ custom: [{ name: "Movie Mode" }] }));
      }).toThrow(/HTPC_BRIDGE_ENDPOINTS/);
    });

    it("rejects a custom entry missing its name", () => {
      expect(() => {
        parseConfig(withEndpoints({ custom: [{ key: "movie-mode" }] }));
      }).toThrow(/HTPC_BRIDGE_ENDPOINTS/);
    });

    it("rejects a custom entry with an empty name", () => {
      expect(() => {
        parseConfig(withEndpoints({ custom: [{ key: "movie-mode", name: "" }] }));
      }).toThrow(/HTPC_BRIDGE_ENDPOINTS/);
    });

    it("rejects an invalid custom key slug (uppercase)", () => {
      expect(() => {
        parseConfig(withEndpoints({ custom: [{ key: "Movie-Mode", name: "M" }] }));
      }).toThrow(/HTPC_BRIDGE_ENDPOINTS/);
    });

    it("rejects an invalid custom key slug (underscore)", () => {
      expect(() => {
        parseConfig(withEndpoints({ custom: [{ key: "movie_mode", name: "M" }] }));
      }).toThrow(/HTPC_BRIDGE_ENDPOINTS/);
    });

    it("rejects a custom key longer than 64 characters", () => {
      expect(() => {
        parseConfig(withEndpoints({ custom: [{ key: "k".repeat(65), name: "M" }] }));
      }).toThrow(/HTPC_BRIDGE_ENDPOINTS/);
    });

    it("rejects a stray enabled flag on a custom entry (disabled customs are omitted)", () => {
      expect(() => {
        parseConfig(withEndpoints({ custom: [{ key: "movie-mode", name: "M", enabled: true }] }));
      }).toThrow(/HTPC_BRIDGE_ENDPOINTS/);
    });

    it("rejects a stray action block on a custom entry (the sidecar never executes)", () => {
      expect(() => {
        parseConfig(
          withEndpoints({
            custom: [{ key: "movie-mode", name: "M", action: { type: "mediaKey" } }],
          }),
        );
      }).toThrow(/HTPC_BRIDGE_ENDPOINTS/);
    });

    it("rejects duplicate custom keys as fatal", () => {
      expect(() => {
        parseConfig(
          withEndpoints({
            custom: [
              { key: "movie-mode", name: "Movie Mode" },
              { key: "movie-mode", name: "Movie Mode Again" },
            ],
          }),
        );
      }).toThrow(/duplicate key "movie-mode"/);
    });
  });

  describe("HTPC_BRIDGE_MOMENTARY_RESET_MS (S7-1)", () => {
    it("defaults to 300 ms when unset (must match the tray app's default)", () => {
      expect(parseConfig(baseEnv()).momentaryResetMs).toBe(300);
    });

    it("treats an empty string like unset", () => {
      expect(parseConfig(baseEnv({ HTPC_BRIDGE_MOMENTARY_RESET_MS: "" })).momentaryResetMs).toBe(
        300,
      );
    });

    it("accepts the range boundaries 0 and 2000 (0 = immediate reset, S8-2)", () => {
      expect(parseConfig(baseEnv({ HTPC_BRIDGE_MOMENTARY_RESET_MS: "0" })).momentaryResetMs).toBe(
        0,
      );
      expect(
        parseConfig(baseEnv({ HTPC_BRIDGE_MOMENTARY_RESET_MS: "2000" })).momentaryResetMs,
      ).toBe(2000);
    });

    it.each([
      ["below the minimum", "-1"],
      ["above the maximum", "2001"],
      ["a non-integer", "300.5"],
      ["a non-numeric string", "fast"],
    ])("rejects %s (%s) as fatal", (_desc, raw) => {
      expect(() => {
        parseConfig(baseEnv({ HTPC_BRIDGE_MOMENTARY_RESET_MS: raw }));
      }).toThrow(/HTPC_BRIDGE_MOMENTARY_RESET_MS/);
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
