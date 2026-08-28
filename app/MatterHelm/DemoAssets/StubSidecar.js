// S2-5/S4-2 stub sidecar: speaks the real IPC protocol (v2 —
// bridge/src/ipc/protocol.ts / Sidecar/Protocol.cs) over the real WebSocket
// so `--demo-wired` and the wiring tests exercise the tray app end to end
// without matter.js. Reads the BLUEPRINT §2.3 env contract, hellos with the
// session token, runs a scripted action sequence (incl. a v2 `custom`
// action), and — once the demo's sentinel state frame arrives — sends one
// deliberate v1 frame to prove the version bump closes the socket. Logs
// pino-shaped JSON lines to stdout (the supervisor maps them into the app
// log) and exits cleanly on stdin EOF — the supervisor's shutdown tether.
// Node 22: global WebSocket + crypto.randomUUID.
'use strict';

const port = process.env.HTPC_BRIDGE_IPC_PORT;
const token = process.env.HTPC_BRIDGE_IPC_TOKEN;

function log(level, msg) {
  console.log(JSON.stringify({ level, msg }));
}

if (!port || !token) {
  log(50, 'stub: HTPC_BRIDGE_IPC_PORT/HTPC_BRIDGE_IPC_TOKEN not set');
  process.exit(1);
}

// Stdin tether: the tray app closes our stdin to ask us to exit.
process.stdin.resume();
process.stdin.on('end', () => {
  log(30, 'stub: stdin EOF; exiting');
  process.exit(0);
});

const ws = new WebSocket(`ws://localhost:${port}/`);
const send = (frame) => ws.send(JSON.stringify(frame));

function sendAction(name, value) {
  const id = crypto.randomUUID();
  const frame =
    value === undefined
      ? { v: 3, type: 'action', id, name }
      : { v: 3, type: 'action', id, name, value };
  send(frame);
  log(30, `stub-sent ${name} ${id}`);
}

function sendCustom(key) {
  const id = crypto.randomUUID();
  send({ v: 3, type: 'action', id, name: 'custom', key });
  log(30, `stub-sent custom ${id}`);
}

ws.addEventListener('open', () => {
  log(30, 'stub: connected; sending hello');
  send({ v: 3, type: 'hello', token, protocol: 1 });
  // The stub models a COMMISSIONED bridge, so it must report matterStatus:
  // since S10-12 the tray only reaches Connected (green) once a matterStatus
  // frame arrives — without it the demo would sit on Running (amber) forever.
  // commissioned=true pairs with advertisement="notApplicable" (protocol
  // cross-field invariant, BLUEPRINT §2.3).
  setTimeout(
    () =>
      send({
        v: 3,
        type: 'matterStatus',
        commissioned: true,
        advertisement: 'notApplicable',
      }),
    150,
  );
  setTimeout(() => sendAction('setVolume', 37), 300);
  setTimeout(() => sendAction('setMuted', false), 800);
  setTimeout(() => sendAction('playPause'), 1300);
  setTimeout(() => sendCustom('demo-note'), 1550);
  setTimeout(() => {
    send({
      v: 3,
      type: 'pairing',
      qrPayload: 'MT:STUB-DEMO-PAYLOAD',
      manualCode: '3497-011-2332',
    });
    log(30, 'stub: pairing frame sent');
  }, 1800);
});

// Every inbound frame (acks, state) is echoed for the demo/tests to assert
// on. The demo's sentinel volume (61) is the cue that the scripted sequence
// was fully observed — then send one v1 frame: the tray app must reject it
// and close the socket (version-bump proof, ADR-004 §3).
let v1ProofSent = false;
ws.addEventListener('message', (event) => {
  log(30, `stub-recv ${event.data}`);
  if (!v1ProofSent && String(event.data).includes('"volume":61')) {
    v1ProofSent = true;
    send({ v: 1, type: 'action', id: crypto.randomUUID(), name: 'playPause' });
    log(30, 'stub: v1 frame sent (expecting rejection + close)');
  }
});
ws.addEventListener('close', () => log(40, 'stub: socket closed'));
ws.addEventListener('error', () => log(40, 'stub: socket error'));
