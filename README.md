# windows-google-home-matter

**HTPC Matter Bridge** — a standalone Windows tray application that makes your
HTPC a **locally-paired Google Home device**. Say "Hey Google, set HTPC volume
to 40 %", tap devices in the Home app, or wire routines ("movie time") — the
app executes the media/volume/power actions directly on the PC.

No cloud, no Google developer account, no OAuth: pairing is a QR-code scan
(Matter over the local network via a [matter.js](https://github.com/project-chip/matter.js/)
virtual bridge). Fully independent product — see `docs/adr/001` (it shares a
machine, but no code, with the VoiceRemote voice-control app).

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

**Phase: blueprint.** Research done (`docs/RESEARCH.md`), standalone design
fixed (`docs/BLUEPRINT.md`, ADR-001), agile plan + agent-ready backlog written
(`docs/DEVELOPMENT-PLAN.md`, `BACKLOG.md`). Sprint 0 starts with hardware
spikes (pairing an uncertified bridge, momentary-switch UX).

## Repository layout

| Path | Purpose |
|---|---|
| `bridge/` | Matter sidecar — Node 22 + TypeScript (strict), matter.js |
| `app/` | Tray application — C# .NET 8 WinForms (`HtpcMatterBridge`) |
| `docs/BLUEPRINT.md` | Binding architecture & protocol spec |
| `docs/RESEARCH.md` | Integration-route research (July 2026) |
| `docs/DEVELOPMENT-PLAN.md` · `BACKLOG.md` | Process, sprints, story backlog |
| `docs/ENGINEERING-STANDARDS.md` | Quality bar (TS + C#) |
| `docs/adr/` | Architecture Decision Records |
| `CLAUDE.md` | Sub-agent orchestration playbook |

## Quick start (once sprints land)

```bash
cd bridge && npm ci && npm run verify        # sidecar: lint + types + tests
# app:  dotnet build app/HtpcMatterBridge/HtpcMatterBridge.csproj -c Release
# dist: ./build.ps1  → single folder with both executables
```

Requires Node 22 LTS (dev) — end users get a single-exe sidecar (Node SEA)
bundled next to the tray app.
