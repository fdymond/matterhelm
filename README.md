# windows-google-home-matter

Expose a Windows HTPC as a **locally-paired Google Home device** using a
[matter.js](https://github.com/project-chip/matter.js/) virtual-device bridge —
no Google developer account, no OAuth server, no cloud webhook. Pairing is a QR
code scan in the Google Home app.

The bridge is a Node.js/TypeScript sidecar driven by
[VoiceRemote](../windows-voice-control) (the offline voice remote tray app):
Google Home becomes *another input modality* alongside the wake word, dispatching
into the same `CommandRouter` actions (play/pause, next/previous, volume, mute,
power) over a localhost IPC channel.

```
Google Home app / Nest speaker ("Hey Google, set HTPC volume to 40%")
        │  Matter (local network, TCP/UDP + mDNS)
        ▼
matter-bridge sidecar (this repo, Node.js)          ←  you are here
        │  localhost WebSocket (JSON, token-auth)
        ▼
VoiceRemote tray app (C#) → CommandRouter → SMTC / media keys / CoreAudio / Kodi
```

## Status

**Phase: blueprint.** Research is complete (`docs/RESEARCH.md`), the technical
design is written (`docs/BLUEPRINT.md`), and the delivery plan with a
story-level backlog is ready (`docs/DEVELOPMENT-PLAN.md`, `BACKLOG.md`).
Implementation has not started; Sprint 0 begins with the riskiest-assumption
spike (pairing a minimal device with a real Google Home).

## Repository layout

| Path | Purpose |
|---|---|
| `docs/RESEARCH.md` | Integration-route research & findings (July 2026) |
| `docs/BLUEPRINT.md` | Architecture, device model, IPC protocol spec |
| `docs/DEVELOPMENT-PLAN.md` | Agile process, sprints, quality gates, CI |
| `docs/ENGINEERING-STANDARDS.md` | Code standards & principles (TS + C# side) |
| `docs/adr/` | Architecture Decision Records |
| `BACKLOG.md` | Story-level backlog sized for sub-agent delegation |
| `CLAUDE.md` | Orchestration playbook for AI sub-agents working here |
| `src/` | Sidecar source (TypeScript) |
| `test/` | Vitest suites |

## Quick start (once Sprint 0 lands)

```bash
npm install
npm run build     # tsc strict
npm test          # vitest
npm start         # runs the bridge; prints pairing QR on first run
```

Requires Node 22 LTS. The C#-side IPC client lives in the VoiceRemote repo.
