/**
 * Environment/config parsing (docs/BLUEPRINT.md §2.3's env contract table is
 * normative — this module is its executable form for the sidecar side).
 *
 * `parseConfig` is pure: given a plain env record it returns a validated
 * {@link Config} or throws a plain `Error` with a message naming the bad
 * variable — never the whole env, so the session token can never end up in
 * an error about some other field. `loadConfig` is the only impure entry
 * point (reads `process.env`); the composition root (`index.ts`) is the only
 * intended caller.
 *
 * This is a trust boundary (ENGINEERING-STANDARDS.md: "parse, don't
 * validate-and-hope") — every field is zod-validated or explicitly checked
 * before use, and a malformed value is a fatal startup error, never a silent
 * fallback.
 */
import { join } from "node:path";

import { z } from "zod";

import type { DeviceNames } from "./matter/devices.js";

const PORT_MIN = 1024;
const PORT_MAX = 65535;
const DEFAULT_IPC_PORT = 39531;
const DEFAULT_LOG_LEVEL = "info";

const DEFAULT_DEVICE_NAMES: DeviceNames = {
  speaker: "HTPC Speaker",
  playPause: "HTPC Play Pause",
  next: "HTPC Next",
  previous: "HTPC Previous",
  power: "HTPC Power",
};

/** pino's own level vocabulary, incl. `silent` (BLUEPRINT §2.3). */
const LogLevelSchema = z.enum(["fatal", "error", "warn", "info", "debug", "trace", "silent"]);
export type PinoLevel = z.infer<typeof LogLevelSchema>;

/** `HTPC_BRIDGE_DEVICE_NAMES` shape: all fields optional, unknown keys rejected. */
const DeviceNamesSchema = z.strictObject({
  speaker: z.string().min(1).optional(),
  playPause: z.string().min(1).optional(),
  next: z.string().min(1).optional(),
  previous: z.string().min(1).optional(),
  power: z.string().min(1).optional(),
});

/** Validated, defaulted bridge configuration — §2.3's env contract table. */
export interface Config {
  /** `HTPC_BRIDGE_IPC_PORT`; default {@link DEFAULT_IPC_PORT}. */
  ipcPort: number;
  /** `HTPC_BRIDGE_IPC_TOKEN`; required, never logged. */
  ipcToken: string;
  /** `HTPC_BRIDGE_STORAGE_DIR`; default `%APPDATA%\HtpcMatterBridge\matter`. */
  storageDir: string;
  /** `HTPC_BRIDGE_LOG_LEVEL`; default `"info"`. */
  logLevel: PinoLevel;
  /** `HTPC_BRIDGE_DEVICE_NAMES`; defaults to the built-in "HTPC …" names. */
  deviceNames: DeviceNames;
  /** `HTPC_BRIDGE_MDNS_INTERFACE`; unset = matter.js auto-detects. */
  mdnsInterface?: string;
  /**
   * `HTPC_BRIDGE_MATTER_PORT` — dev-only escape hatch for running the real
   * bridge beside the S0-3 spike (which holds UDP 5540); unset lets matter.js
   * default to 5540. Not part of the tray app's env contract.
   */
  matterPort?: number;
}

/**
 * Parses an optional integer-port env var, or `undefined` when unset/empty.
 * Throws (naming `varName`, never the value of any other var) on a
 * non-integer or out-of-range value.
 */
function parsePortEnv(raw: string | undefined, varName: string): number | undefined {
  if (raw === undefined || raw === "") {
    return undefined;
  }
  const result = z.coerce.number().int().min(PORT_MIN).max(PORT_MAX).safeParse(raw);
  if (!result.success) {
    throw new Error(
      `${varName} must be an integer between ${String(PORT_MIN)} and ${String(PORT_MAX)}, got ${JSON.stringify(raw)}`,
    );
  }
  return result.data;
}

/** Required, non-empty session token. Never echoed into any error message. */
function parseToken(raw: string | undefined): string {
  const result = z.string().min(1).safeParse(raw);
  if (!result.success) {
    throw new Error(
      "HTPC_BRIDGE_IPC_TOKEN is required and must be a non-empty string " +
        "(the tray app supervisor sets this per session)",
    );
  }
  return result.data;
}

/** Explicit dir, or `%APPDATA%\HtpcMatterBridge\matter` when unset. */
function parseStorageDir(raw: string | undefined, appData: string | undefined): string {
  if (raw !== undefined && raw !== "") {
    return raw;
  }
  if (appData === undefined || appData === "") {
    throw new Error(
      "HTPC_BRIDGE_STORAGE_DIR is unset and APPDATA is unavailable to derive its default " +
        "(%APPDATA%\\HtpcMatterBridge\\matter); set HTPC_BRIDGE_STORAGE_DIR explicitly",
    );
  }
  return join(appData, "HtpcMatterBridge", "matter");
}

function parseLogLevel(raw: string | undefined): PinoLevel {
  if (raw === undefined || raw === "") {
    return DEFAULT_LOG_LEVEL;
  }
  const result = LogLevelSchema.safeParse(raw);
  if (!result.success) {
    throw new Error(
      `HTPC_BRIDGE_LOG_LEVEL must be one of ${LogLevelSchema.options.join(", ")}, got ${JSON.stringify(raw)}`,
    );
  }
  return result.data;
}

/** Unset/empty = built-in defaults; malformed JSON or shape is fatal, not silent. */
function parseDeviceNames(raw: string | undefined): DeviceNames {
  if (raw === undefined || raw === "") {
    return DEFAULT_DEVICE_NAMES;
  }
  let json: unknown;
  try {
    json = JSON.parse(raw);
  } catch {
    throw new Error("HTPC_BRIDGE_DEVICE_NAMES is not valid JSON");
  }
  const result = DeviceNamesSchema.safeParse(json);
  if (!result.success) {
    const issues = result.error.issues
      .map((issue) => `${issue.path.join(".") || "(root)"}: ${issue.message}`)
      .join("; ");
    throw new Error(
      "HTPC_BRIDGE_DEVICE_NAMES must be a JSON object with optional non-empty string fields " +
        `speaker/playPause/next/previous/power: ${issues}`,
    );
  }
  // Field-by-field merge (not `{...DEFAULT_DEVICE_NAMES, ...result.data}`):
  // zod's `.optional()` types each field `string | undefined`, and spreading
  // that in would give the result the same `| undefined` type even though no
  // key is ever actually absent — incompatible with `DeviceNames`'s required
  // `string` fields under `exactOptionalPropertyTypes`.
  return {
    speaker: result.data.speaker ?? DEFAULT_DEVICE_NAMES.speaker,
    playPause: result.data.playPause ?? DEFAULT_DEVICE_NAMES.playPause,
    next: result.data.next ?? DEFAULT_DEVICE_NAMES.next,
    previous: result.data.previous ?? DEFAULT_DEVICE_NAMES.previous,
    power: result.data.power ?? DEFAULT_DEVICE_NAMES.power,
  };
}

function parseMdnsInterface(raw: string | undefined): string | undefined {
  return raw === undefined || raw === "" ? undefined : raw;
}

/**
 * Parses a plain env record (never the live process — see {@link loadConfig})
 * into a validated {@link Config}, or throws a plain `Error` describing
 * exactly which variable is wrong. Pure: no I/O, no defaults beyond what
 * `env` itself supplies.
 */
export function parseConfig(env: Record<string, string | undefined>): Config {
  const mdnsInterface = parseMdnsInterface(env.HTPC_BRIDGE_MDNS_INTERFACE);
  const matterPort = parsePortEnv(env.HTPC_BRIDGE_MATTER_PORT, "HTPC_BRIDGE_MATTER_PORT");
  return {
    ipcPort: parsePortEnv(env.HTPC_BRIDGE_IPC_PORT, "HTPC_BRIDGE_IPC_PORT") ?? DEFAULT_IPC_PORT,
    ipcToken: parseToken(env.HTPC_BRIDGE_IPC_TOKEN),
    storageDir: parseStorageDir(env.HTPC_BRIDGE_STORAGE_DIR, env.APPDATA),
    logLevel: parseLogLevel(env.HTPC_BRIDGE_LOG_LEVEL),
    deviceNames: parseDeviceNames(env.HTPC_BRIDGE_DEVICE_NAMES),
    ...(mdnsInterface === undefined ? {} : { mdnsInterface }),
    ...(matterPort === undefined ? {} : { matterPort }),
  };
}

/** Reads live `process.env`. The only impure entry point in this module. */
export function loadConfig(): Config {
  return parseConfig(process.env);
}
