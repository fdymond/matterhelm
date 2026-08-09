/**
 * pino logger factory (docs/BLUEPRINT.md §2.1: "log.ts pino, one structured
 * line per event"). The composition root is the only caller; every other
 * module receives a logger instance rather than constructing its own.
 */
import pino from "pino";
import type { Logger } from "pino";

import type { PinoLevel } from "./config.js";

/**
 * Builds the process's one pino logger at the configured level. `base: null`
 * drops pino's default `pid`/`hostname` fields — ENGINEERING-STANDARDS.md
 * calls for minimal base fields, and neither is useful on a single-purpose
 * loopback sidecar with one process per session.
 */
export function makeLogger(level: PinoLevel): Logger {
  return pino({ level, base: null });
}
