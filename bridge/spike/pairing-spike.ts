/**
 * S0-3 pairing spike — THROWAWAY code, not product architecture.
 *
 * A minimal matter.js bridge (Aggregator + Speaker + one On/Off Plug-in Unit)
 * used to validate Google Home commissioning of an uncertified test-VID bridge
 * and the Speaker volume UX. Human checklist: docs/spikes/S0-3-pairing.md.
 *
 * Run from bridge/:  npx tsx spike/pairing-spike.ts
 * Factory reset:     delete bridge/spike/spike-storage/
 *
 * Production code keeps all matter.js imports behind src/matter/adapter.ts
 * (BLUEPRINT section 2.1); this spike imports matter.js directly on purpose.
 */
/* eslint-disable no-console -- spike: console output (pairing codes + EVENT lines) IS the deliverable */

import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { Endpoint, Environment, ServerNode, VendorId } from "@matter/main";
import { BridgedDeviceBasicInformationServer } from "@matter/main/behaviors/bridged-device-basic-information";
import { OnOffPlugInUnitDevice } from "@matter/main/devices/on-off-plug-in-unit";
import { SpeakerDevice } from "@matter/main/devices/speaker";
import { AggregatorEndpoint } from "@matter/main/endpoints/aggregator";
// QrCode (terminal ASCII QR renderer) lives in @matter/types, a direct
// dependency of @matter/main pinned at the same version by the lockfile;
// @matter/main does not re-export it. Spike-only shortcut.
import { QrCode } from "@matter/types/schema";

const spikeDir = dirname(fileURLToPath(import.meta.url));
// Ignored by git and prettier (bridge/.prettierignore covers the runtime
// driver.json matter.js writes here). Factory reset = delete spike-storage/.
const storageDir = join(spikeDir, "spike-storage");

// Both must be set before ServerNode.create() initializes the environment:
// - storage.path: keep fabric credentials next to the spike (gitignored) so
//   "factory reset" is just deleting the directory.
// - runtime.signals=false: matter.js's NodeJsEnvironment would otherwise
//   install its own SIGINT handler; we own Ctrl+C shutdown below.
Environment.default.vars.set("storage.path", storageDir);
Environment.default.vars.set("runtime.signals", false);

const server = await ServerNode.create({
  id: "s0-3-pairing-spike",
  productDescription: {
    name: "HTPC Bridge Spike",
    deviceType: AggregatorEndpoint.deviceType,
  },
  basicInformation: {
    // Sanctioned Matter test VID/PID — must match the Google Home Developer
    // Console project integration exactly (ADR-002).
    vendorId: VendorId(0xfff1),
    productId: 0x8000,
    vendorName: "HTPC Spike",
    productName: "HTPC Bridge Spike",
    productLabel: "HTPC Bridge Spike",
    nodeLabel: "HTPC Bridge Spike",
    serialNumber: "htpc-spike-0001",
    uniqueId: "f81aa10cbridge",
  },
});

const aggregator = new Endpoint(AggregatorEndpoint, { id: "aggregator" });
await server.add(aggregator);

const speaker = new Endpoint(SpeakerDevice.with(BridgedDeviceBasicInformationServer), {
  id: "speaker",
  bridgedDeviceBasicInformation: {
    nodeLabel: "HTPC Speaker",
    productName: "HTPC Speaker",
    productLabel: "HTPC Speaker",
    serialNumber: "htpc-speaker-0001",
    uniqueId: "f81aa10cspeakr",
    reachable: true,
  },
});
await aggregator.add(speaker);

const playPause = new Endpoint(OnOffPlugInUnitDevice.with(BridgedDeviceBasicInformationServer), {
  id: "playpause",
  bridgedDeviceBasicInformation: {
    nodeLabel: "HTPC Play Pause",
    productName: "HTPC Play Pause",
    productLabel: "HTPC Play Pause",
    serialNumber: "htpc-playpause-0001",
    uniqueId: "f81aa10cplypse",
    reachable: true,
  },
});
await aggregator.add(playPause);

// EVENT lines — the human eyeballs these while driving voice/app commands.
speaker.events.onOff.onOff$Changed.on((value) => {
  console.log(`EVENT speaker onOff=${String(value)} (${value ? "unmuted" : "muted"})`);
});
speaker.events.levelControl.currentLevel$Changed.on((value) => {
  const percent = value === null ? "?" : String(Math.round((value / 254) * 100));
  console.log(`EVENT speaker level=${String(value)} (~${percent}%)`);
});
playPause.events.onOff.onOff$Changed.on((value) => {
  console.log(`EVENT playpause onOff=${String(value)}`);
});

server.lifecycle.commissioned.on(() => {
  console.log("SPIKE commissioned — Google Home has joined a fabric with this bridge");
});
server.lifecycle.decommissioned.on(() => {
  console.log("SPIKE decommissioned — the controller removed this bridge");
});

await server.start();

const banner = "=".repeat(72);
console.log(`\n${banner}`);
if (server.lifecycle.isCommissioned) {
  console.log("SPIKE already commissioned — storage persisted, no re-pairing needed.");
  console.log(`SPIKE to factory reset: stop (Ctrl+C), delete ${storageDir}, start again.`);
} else {
  const { qrPairingCode, manualPairingCode } = server.state.commissioning.pairingCodes;
  console.log("SPIKE ready to pair. Google Home app -> + Add device -> Matter-enabled");
  console.log("SPIKE device, then scan this QR code (or type the manual code):");
  console.log(QrCode.get(qrPairingCode));
  console.log(`SPIKE QR pairing payload:  ${qrPairingCode}`);
  console.log(`SPIKE manual pairing code: ${manualPairingCode}`);
}
console.log(`SPIKE storage dir: ${storageDir}`);
console.log("SPIKE watching for cluster writes — EVENT lines appear below. Ctrl+C stops.");
console.log(banner);

let closing = false;
process.on("SIGINT", () => {
  if (closing) {
    return;
  }
  closing = true;
  console.log("\nSPIKE shutting down (Ctrl+C) — closing Matter server...");
  void server
    .close()
    .catch((error: unknown) => {
      console.error("SPIKE close failed:", error);
    })
    .finally(() => {
      console.log("SPIKE stopped.");
      process.exit(0);
    });
});
