/**
 * Environment/config parsing (docs/BLUEPRINT.md §2.3's env contract table,
 * as amended by ADR-004 §2, is normative — this module is its executable
 * form for the sidecar side).
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
 *
 * Naming note (S7-2): the `HTPC_BRIDGE_*` env prefix deliberately survives
 * the MatterHelm product rename. It is an internal contract between the tray
 * app and this sidecar — we own both sides — so renaming it would be pure
 * churn with drift risk and zero user-visible value.
 */
import { join } from "node:path";

import { z } from "zod";

import { CustomCommandKeySchema } from "./ipc/protocol.js";
import type { BuiltinEndpointKey, EndpointsConfig } from "./matter/devices.js";
import { MATTER_LOG_LEVELS } from "./matter/diagnostics.js";
import type { MatterLogLevel } from "./matter/diagnostics.js";

const PORT_MIN = 1024;
const PORT_MAX = 65535;
const DEFAULT_IPC_PORT = 39531;
const DEFAULT_LOG_LEVEL = "info";

/**
 * `HTPC_BRIDGE_MOMENTARY_RESET_MS` bounds and default (S7-1, owner request).
 * 0 = reset on the next tick after the On command — safe since ADR-008 made
 * the window presentation-only (dispatch happens on the command itself), and
 * the fastest tile snap-back Google's controller model allows (S8-2).
 */
const MOMENTARY_RESET_MS_MIN = 0;
const MOMENTARY_RESET_MS_MAX = 2000;
const DEFAULT_MOMENTARY_RESET_MS = 0;

/**
 * Built-in display-name defaults (BLUEPRINT §2.2's "HTPC …" voice targets).
 * Deliberately NOT renamed to MatterHelm (S7-2): these are the user-facing
 * paired device names — "HTPC Speaker" is what people say to Google — and
 * changing the defaults would churn fresh installs for no gain.
 */
const DEFAULT_BUILTIN_NAMES: Readonly<Record<BuiltinEndpointKey, string>> = {
  speaker: "HTPC Speaker",
  playPause: "HTPC Play Pause",
  next: "HTPC Next",
  previous: "HTPC Previous",
  power: "HTPC Power",
};

/** pino's own level vocabulary, incl. `silent` (BLUEPRINT §2.3). */
const LogLevelSchema = z.enum(["fatal", "error", "warn", "info", "debug", "trace", "silent"]);
export type PinoLevel = z.infer<typeof LogLevelSchema>;

/** matter.js's level vocabulary (ADR-006 §1; matter/diagnostics.ts owns it). */
const MatterLogLevelSchema = z.enum(MATTER_LOG_LEVELS);

/**
 * `HTPC_BRIDGE_MATTER_LOG_FACILITIES` value shape: facility name -> matter.js
 * level. Facility names are matter.js's own logger names (e.g. `MdnsServer`,
 * `SessionManager`) — free-form here beyond non-emptiness, since the set is
 * a matter.js internal that changes between releases.
 */
const MatterLogFacilitiesSchema = z.record(z.string().min(1), MatterLogLevelSchema);

/**
 * One built-in entry inside `HTPC_BRIDGE_ENDPOINTS` (ADR-004 §2). The tray
 * app always sends both fields; each is still individually defaulted here
 * (name -> the "HTPC …" default, enabled -> true) so a hand-written partial
 * env keeps the pre-ADR-004 `HTPC_BRIDGE_DEVICE_NAMES` leniency. Unknown
 * keys and wrong types stay fatal.
 */
const BuiltinEndpointSchema = z.strictObject({
  name: z.string().min(1).optional(),
  enabled: z.boolean().optional(),
});

/**
 * One custom command entry (ADR-004 §2): names + existence only — the
 * sidecar never learns what a command *does*, and disabled customs are
 * omitted by the tray app, so no `enabled`/`action` field is legal here.
 */
const CustomEndpointSchema = z.strictObject({
  key: CustomCommandKeySchema,
  name: z.string().min(1),
});

/** `HTPC_BRIDGE_ENDPOINTS` shape (ADR-004 §2): built-ins + `custom` list. */
const EndpointsSchema = z.strictObject({
  speaker: BuiltinEndpointSchema.optional(),
  playPause: BuiltinEndpointSchema.optional(),
  next: BuiltinEndpointSchema.optional(),
  previous: BuiltinEndpointSchema.optional(),
  power: BuiltinEndpointSchema.optional(),
  custom: z.array(CustomEndpointSchema).optional(),
});

/** Validated, defaulted bridge configuration — §2.3's env contract table. */
export interface Config {
  /** `HTPC_BRIDGE_IPC_PORT`; default {@link DEFAULT_IPC_PORT}. */
  ipcPort: number;
  /** `HTPC_BRIDGE_IPC_TOKEN`; required, never logged. */
  ipcToken: string;
  /** `HTPC_BRIDGE_STORAGE_DIR`; default `%APPDATA%\MatterHelm\matter`. */
  storageDir: string;
  /** `HTPC_BRIDGE_LOG_LEVEL`; default `"info"`. */
  logLevel: PinoLevel;
  /**
   * `HTPC_BRIDGE_MATTER_LOG_LEVEL` (ADR-006 §1) — matter.js's global log
   * level; default derived from {@link logLevel} via
   * {@link defaultMatterLogLevel}.
   */
  matterLogLevel: MatterLogLevel;
  /**
   * `HTPC_BRIDGE_MATTER_LOG_FACILITIES` (ADR-006 §1) — per-facility matter.js
   * level overrides (JSON object facility -> level), merged over
   * {@link DEFAULT_MATTER_LOG_FACILITIES} (user entries win).
   */
  matterLogFacilities: Readonly<Record<string, MatterLogLevel>>;
  /**
   * `HTPC_BRIDGE_ENDPOINTS` (ADR-004 §2); default: every built-in enabled
   * with its "HTPC …" name, no custom commands.
   */
  endpoints: EndpointsConfig;
  /**
   * `HTPC_BRIDGE_MOMENTARY_RESET_MS` (S7-1) — how long after an `on` write a
   * momentary endpoint snaps back to `off`, in ms (integer 0–2000; default
   * {@link DEFAULT_MOMENTARY_RESET_MS}). Both sides of the env contract share
   * the 0 ms default (immediate since S8-2) — the tray app's `momentaryResetMs` config field must
   * stay in lockstep.
   */
  momentaryResetMs: number;
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

/**
 * Explicit dir, or `%APPDATA%\MatterHelm\matter` when unset (S7-2 rename).
 * The tray app always sets `HTPC_BRIDGE_STORAGE_DIR` explicitly from its own
 * migrated (or fallback) data root, so this default only serves hand-run dev
 * sessions — which get fresh state under the new name.
 */
function parseStorageDir(raw: string | undefined, appData: string | undefined): string {
  if (raw !== undefined && raw !== "") {
    return raw;
  }
  if (appData === undefined || appData === "") {
    throw new Error(
      "HTPC_BRIDGE_STORAGE_DIR is unset and APPDATA is unavailable to derive its default " +
        "(%APPDATA%\\MatterHelm\\matter); set HTPC_BRIDGE_STORAGE_DIR explicitly",
    );
  }
  return join(appData, "MatterHelm", "matter");
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

/**
 * The matter.js global level implied by our own log level when
 * `HTPC_BRIDGE_MATTER_LOG_LEVEL` is unset (ADR-006 §1). Deliberately one
 * notch quieter around the default: matter.js INFO narrates every exchange,
 * so a bridge running at pino `info` gets matter.js `notice` (state changes
 * and problems, not chatter). pino `silent` maps to `fatal` — the quietest
 * matter.js level; the forwarded events land in a silent pino logger and are
 * dropped there anyway.
 */
export function defaultMatterLogLevel(logLevel: PinoLevel): MatterLogLevel {
  switch (logLevel) {
    case "trace":
    case "debug":
      return "debug";
    case "info":
      return "notice";
    case "warn":
      return "warn";
    case "error":
      return "error";
    case "fatal":
    case "silent":
      return "fatal";
  }
}

function parseMatterLogLevel(raw: string | undefined, logLevel: PinoLevel): MatterLogLevel {
  if (raw === undefined || raw === "") {
    return defaultMatterLogLevel(logLevel);
  }
  const result = MatterLogLevelSchema.safeParse(raw);
  if (!result.success) {
    throw new Error(
      `HTPC_BRIDGE_MATTER_LOG_LEVEL must be one of ${MATTER_LOG_LEVELS.join(", ")} ` +
        `(matter.js's level names), got ${JSON.stringify(raw)}`,
    );
  }
  return result.data;
}

/**
 * Unset/empty = no per-facility overrides; malformed JSON, a non-object, an
 * empty facility name, or an unknown level are all fatal, never silent —
 * same contract as `HTPC_BRIDGE_ENDPOINTS` (ADR-006 §1).
 */
function parseMatterLogFacilities(
  raw: string | undefined,
): Readonly<Record<string, MatterLogLevel>> | undefined {
  if (raw === undefined || raw === "") {
    return undefined;
  }
  let json: unknown;
  try {
    json = JSON.parse(raw);
  } catch {
    throw new Error("HTPC_BRIDGE_MATTER_LOG_FACILITIES is not valid JSON");
  }
  if (typeof json !== "object" || json === null || Array.isArray(json)) {
    throw new Error(
      "HTPC_BRIDGE_MATTER_LOG_FACILITIES must be a JSON object mapping facility names " +
        `to one of ${MATTER_LOG_LEVELS.join(", ")}`,
    );
  }
  const result = MatterLogFacilitiesSchema.safeParse(json);
  if (!result.success) {
    const issues = result.error.issues
      .map((issue) => `${issue.path.join(".") || "(root)"}: ${issue.message}`)
      .join("; ");
    throw new Error(
      "HTPC_BRIDGE_MATTER_LOG_FACILITIES must be a JSON object mapping facility names " +
        `to one of ${MATTER_LOG_LEVELS.join(", ")}: ${issues}`,
    );
  }
  return result.data;
}

/**
 * Always-applied facility floor (S5-R finding F1): matter.js's Commissioning
 * facility logs the RAW setup passcode, manual pairing code, and QR payload
 * at NOTICE — which passes our default threshold, flows through the pino
 * destination onto stdout, into the tray app's persisted log, and from there
 * into the exportable diagnostics bundle. The pairing codes already reach
 * the UI structured via the `pairing` IPC frame, so the log line is
 * redundant — suppress below WARN by default. An explicit user override for
 * `Commissioning` (e.g. during a pairing spike) wins over this default.
 */
export const DEFAULT_MATTER_LOG_FACILITIES: Readonly<Record<string, MatterLogLevel>> = {
  Commissioning: "warn",
};

/** Every built-in enabled under its default name, no custom commands. */
function defaultEndpoints(): EndpointsConfig {
  return {
    speaker: { name: DEFAULT_BUILTIN_NAMES.speaker, enabled: true },
    playPause: { name: DEFAULT_BUILTIN_NAMES.playPause, enabled: true },
    next: { name: DEFAULT_BUILTIN_NAMES.next, enabled: true },
    previous: { name: DEFAULT_BUILTIN_NAMES.previous, enabled: true },
    power: { name: DEFAULT_BUILTIN_NAMES.power, enabled: true },
    custom: [],
  };
}

/**
 * Unset/empty = built-in defaults (all enabled, no custom); malformed JSON,
 * a bad shape, an invalid custom key slug, or duplicate custom keys are all
 * fatal, never silent (ADR-004 §2).
 */
function parseEndpoints(raw: string | undefined): EndpointsConfig {
  if (raw === undefined || raw === "") {
    return defaultEndpoints();
  }
  let json: unknown;
  try {
    json = JSON.parse(raw);
  } catch {
    throw new Error("HTPC_BRIDGE_ENDPOINTS is not valid JSON");
  }
  const result = EndpointsSchema.safeParse(json);
  if (!result.success) {
    const issues = result.error.issues
      .map((issue) => `${issue.path.join(".") || "(root)"}: ${issue.message}`)
      .join("; ");
    throw new Error(
      "HTPC_BRIDGE_ENDPOINTS must be the ADR-004 §2 JSON object " +
        "(built-ins {name, enabled} + custom [{key, name}]): " +
        issues,
    );
  }
  const custom = result.data.custom ?? [];
  const seen = new Set<string>();
  for (const entry of custom) {
    if (seen.has(entry.key)) {
      throw new Error(
        `HTPC_BRIDGE_ENDPOINTS custom keys must be unique; duplicate key ${JSON.stringify(entry.key)}`,
      );
    }
    seen.add(entry.key);
  }
  // Field-by-field defaulting (not object spread): zod's `.optional()` types
  // each field `T | undefined`, and spreading that in would leak the
  // `| undefined` into the required `EndpointsConfig` fields under
  // exactOptionalPropertyTypes.
  const builtin = (key: BuiltinEndpointKey): { name: string; enabled: boolean } => ({
    name: result.data[key]?.name ?? DEFAULT_BUILTIN_NAMES[key],
    enabled: result.data[key]?.enabled ?? true,
  });
  return {
    speaker: builtin("speaker"),
    playPause: builtin("playPause"),
    next: builtin("next"),
    previous: builtin("previous"),
    power: builtin("power"),
    custom,
  };
}

/**
 * Unset/empty = {@link DEFAULT_MOMENTARY_RESET_MS}; anything but an integer
 * within [{@link MOMENTARY_RESET_MS_MIN}, {@link MOMENTARY_RESET_MS_MAX}] is
 * fatal, never silent — same contract as every other env field.
 */
function parseMomentaryResetMs(raw: string | undefined): number {
  if (raw === undefined || raw === "") {
    return DEFAULT_MOMENTARY_RESET_MS;
  }
  const result = z.coerce
    .number()
    .int()
    .min(MOMENTARY_RESET_MS_MIN)
    .max(MOMENTARY_RESET_MS_MAX)
    .safeParse(raw);
  if (!result.success) {
    throw new Error(
      `HTPC_BRIDGE_MOMENTARY_RESET_MS must be an integer between ${String(MOMENTARY_RESET_MS_MIN)} ` +
        `and ${String(MOMENTARY_RESET_MS_MAX)}, got ${JSON.stringify(raw)}`,
    );
  }
  return result.data;
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
  const logLevel = parseLogLevel(env.HTPC_BRIDGE_LOG_LEVEL);
  const matterLogFacilities = parseMatterLogFacilities(env.HTPC_BRIDGE_MATTER_LOG_FACILITIES);
  return {
    ipcPort: parsePortEnv(env.HTPC_BRIDGE_IPC_PORT, "HTPC_BRIDGE_IPC_PORT") ?? DEFAULT_IPC_PORT,
    ipcToken: parseToken(env.HTPC_BRIDGE_IPC_TOKEN),
    storageDir: parseStorageDir(env.HTPC_BRIDGE_STORAGE_DIR, env.APPDATA),
    logLevel,
    matterLogLevel: parseMatterLogLevel(env.HTPC_BRIDGE_MATTER_LOG_LEVEL, logLevel),
    endpoints: parseEndpoints(env.HTPC_BRIDGE_ENDPOINTS),
    momentaryResetMs: parseMomentaryResetMs(env.HTPC_BRIDGE_MOMENTARY_RESET_MS),
    matterLogFacilities: { ...DEFAULT_MATTER_LOG_FACILITIES, ...matterLogFacilities },
    ...(mdnsInterface === undefined ? {} : { mdnsInterface }),
    ...(matterPort === undefined ? {} : { matterPort }),
  };
}

/** Reads live `process.env`. The only impure entry point in this module. */
export function loadConfig(): Config {
  return parseConfig(process.env);
}
