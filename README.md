# MatterHelm

**MatterHelm** — a standalone Windows tray application that makes your
HTPC a **locally-paired Google Home device**. Say "Hey Google, set HTPC volume
to 40 %", tap devices in the Home app, or wire routines ("movie time") — the
app executes the media/volume/power actions directly on the PC.

No cloud, no OAuth server, no paid certification: pairing is a QR-code scan
(Matter over the local network via a [matter.js](https://github.com/project-chip/matter.js/)
virtual bridge). Two Google-side prerequisites (see `docs/adr/002`): a
Google/Nest **Matter hub** device on the LAN (Nest speaker/display/Wifi Pro/
Google TV Streamer), and a one-time **free** Google Home Developer Console
project registering the bridge's test VID/PID. Fully independent product —
see `docs/adr/001` (it shares a machine, but no code, with the VoiceRemote
voice-control app).

```
Google Home app / Nest speaker
        │  Matter (local network)
        ▼
bridge/  Node 22 + matter.js sidecar        (Matter Aggregator: Speaker + switches)
        │  localhost WebSocket, token-auth
        ▼
app/     C# .NET 8 WinForms tray app        (supervisor + executor + UI)
        ├─ ActionExecutor → SMTC / media keys / CoreAudio / display power
        └─ Overlay HUD    → click-through pop-ups: incoming command + action taken
```

## What you can control

| Google Home surface | Action on the PC |
|---|---|
| "set HTPC volume to 40 %" / volume slider | System volume (CoreAudio) |
| "mute the HTPC speaker" | System mute |
| "turn on HTPC Play Pause" / routine / app tap | Media play-pause toggle |
| "turn on HTPC Next" · "…Previous" | Next / previous track |
| "turn off HTPC Power" | Configurable: pause + screen off / sleep |

(Transport rides on momentary virtual switches — Google Home doesn't yet
support Matter's media-playback cluster; see `docs/RESEARCH.md`.)

## Status

**Phase: Sprint 2 built, awaiting hardware spikes.** CI green on every commit
(bridge verify on ubuntu+windows, app build+tests on windows).

- **Bridge (`bridge/`)**: IPC protocol schemas (S1-1), pure mapping layer with
  lossless volume round-trip (S1-2), and the reconnecting WS client (S1-4) are
  done — 122 tests. The matter.js device model (S1-3) + composition root
  (S1-5) are gated on the S0-3 hardware spike.
- **Tray app (`app/`)**: fully built and wired (S0-5, S2-1…S2-5) — supervisor,
  loopback IPC server, CoreAudio/media-key/display executor, click-through
  overlay HUD, pairing-QR window, config + tray states. 126 tests; a mock-
  sidecar E2E proves action→execute→overlay→ack and state publishing.
  Adversarial review (S2-R) in progress.
- **Next human step**: run `docs/spikes/S0-3-pairing.md` (needs a Nest hub, the
  Google Home app, and a one-time free Developer Console project) to validate
  pairing + Speaker volume UX, then S1-3/S1-5 close the loop for real
  "Hey Google" control.

## Repository layout

| Path | Purpose |
|---|---|
| `bridge/` | Matter sidecar — Node 22 + TypeScript (strict), matter.js |
| `app/` | Tray application — C# .NET WinForms (`MatterHelm`) |
| `docs/BLUEPRINT.md` | Binding architecture & protocol spec |
| `docs/RESEARCH.md` | Integration-route research (July 2026) |
| `docs/DEVELOPMENT-PLAN.md` · `BACKLOG.md` | Process, sprints, story backlog |
| `docs/ENGINEERING-STANDARDS.md` | Quality bar (TS + C#) |
| `docs/adr/` | Architecture Decision Records |
| `CLAUDE.md` | Sub-agent orchestration playbook |

## Quick start (once sprints land)

```bash
cd bridge && npm ci && npm run verify        # sidecar: lint + types + tests
# app:  dotnet build app/MatterHelm/MatterHelm.csproj -c Release
# dist: ./build.ps1  → single folder with both executables
```

Requires Node 22 LTS (dev) — end users get a single-exe sidecar (Node SEA)
bundled next to the tray app.
