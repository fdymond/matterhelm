/**
 * S1-3 boot smoke script (extended for ADR-004 in S4-1) — proves, without a
 * Matter controller, that the bridge constructs the aggregator + the
 * config-derived endpoint set (§2.2 built-ins plus a custom momentary plug,
 * minus a disabled built-in), exposes pairing codes after start, suppresses
 * local-write echoes, and closes cleanly. Not part of `npm run verify` (it
 * binds real network resources); run it manually from bridge/:
 *
 *   npx tsx src/matter/smoke.ts
 *
 * Uses a throwaway storage dir (deleted afterwards) and port 5543 so it can
 * never collide with a real bridge on the default 5540 or its fabric storage.
 */
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { setTimeout as delay } from "node:timers/promises";

import type { ClusterWrite } from "../mapping/actions.js";

import { createBridge } from "./bridge.js";

const out = (line: string): void => {
  process.stdout.write(`${line}\n`);
};

function ok(condition: boolean, label: string): void {
  if (!condition) {
    throw new Error(`SMOKE FAIL: ${label}`);
  }
  out(`SMOKE OK: ${label}`);
}

const storageDir = mkdtempSync(join(tmpdir(), "htpc-bridge-smoke-"));
const writes: ClusterWrite[] = [];

const bridge = await createBridge({
  storageDir,
  port: 5543,
  endpoints: {
    speaker: { name: "HTPC Speaker", enabled: true },
    playPause: { name: "HTPC Play Pause", enabled: true },
    next: { name: "HTPC Next", enabled: false }, // disabled built-in: omitted
    previous: { name: "HTPC Previous", enabled: true },
    power: { name: "HTPC Power", enabled: true },
    custom: [{ key: "movie-mode", name: "Movie Mode" }],
  },
  onClusterWrite: (write) => {
    writes.push(write);
  },
});

try {
  const ids = bridge.endpoints.map((endpoint) => endpoint.id);
  ok(
    ids.join(",") === "speaker,playpause,previous,power,custom-movie-mode",
    `bridge constructed the ADR-004 endpoint set (no disabled 'next'): ${ids.join(",")}`,
  );
  ok(bridge.pairingCodes === null, "pairingCodes is null before start");
  await bridge.start();
  ok(!bridge.isCommissioned, "isCommissioned is false on fresh storage");
  const codes = bridge.pairingCodes;
  if (codes === null) {
    throw new Error("SMOKE FAIL: pairingCodes is null after start");
  }
  ok(codes.qrPayload.startsWith("MT:"), `qrPayload has MT: prefix (${codes.qrPayload})`);
  ok(codes.manualCode.length > 0, `manualCode present (${codes.manualCode})`);

  // Echo suppression: local speaker writes must not re-emit onClusterWrite.
  await bridge.setSpeakerState(127, true);
  await bridge.setSpeakerState(200, false);
  // Momentary endpoints exist and accept writes; already-off resets are no-ops.
  await bridge.resetMomentary("playpause");
  await bridge.resetMomentary("previous");
  await bridge.resetMomentary("custom-movie-mode");
  await delay(250); // let any (erroneous) change events drain
  ok(
    writes.length === 0,
    `local writes emitted no ClusterWrite (echo suppression) — saw ${String(writes.length)}`,
  );
} finally {
  await bridge.close();
  rmSync(storageDir, { recursive: true, force: true });
}

out("SMOKE PASS: clean close, throwaway storage deleted");
