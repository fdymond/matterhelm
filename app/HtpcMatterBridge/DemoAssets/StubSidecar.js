// S2-5 stub sidecar: speaks the real IPC protocol (bridge/src/ipc/protocol.ts
// / Sidecar/Protocol.cs) over the real WebSocket so `--demo-wired` and the
// wiring tests exercise the tray app end to end without matter.js. Reads the
// BLUEPRINT §2.3 env contract, hellos with the session token, runs a scripted
// action sequence, then idles. Logs pino-shaped JSON lines to stdout (the
// supervisor maps them into the app log) and exits cleanly on stdin EOF — the
// supervisor's shutdown tether. Node 22: global WebSocket + crypto.randomUUID.
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
      ? { v: 1, type: 'action', id, name }
      : { v: 1, type: 'action', id, name, value };
  send(frame);
  log(30, `stub-sent ${name} ${id}`);
}

ws.addEventListener('open', () => {
  log(30, 'stub: connected; sending hello');
  send({ v: 1, type: 'hello', token, protocol: 1 });
  setTimeout(() => sendAction('setVolume', 37), 300);
  setTimeout(() => sendAction('setMuted', false), 800);
  setTimeout(() => sendAction('playPause'), 1300);
  setTimeout(() => {
    send({
      v: 1,
      type: 'pairing',
      qrPayload: 'MT:STUB-DEMO-PAYLOAD',
      manualCode: '3497-011-2332',
    });
    log(30, 'stub: pairing frame sent');
  }, 1800);
});

// Every inbound frame (acks, state) is echoed for the demo/tests to assert on.
ws.addEventListener('message', (event) => log(30, `stub-recv ${event.data}`));
ws.addEventListener('close', () => log(40, 'stub: socket closed'));
ws.addEventListener('error', () => log(40, 'stub: socket error'));
