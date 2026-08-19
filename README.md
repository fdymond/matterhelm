# MatterHelm

[![CI](https://github.com/fdymond/matterhelm/actions/workflows/ci.yml/badge.svg)](https://github.com/fdymond/matterhelm/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

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

**Phase: v0.2.0 released** (Sprint 8 trigger mechanics — see
`CHANGELOG.md`). CI green on every commit (bridge verify+coverage on
ubuntu/windows, app build+tests+coverage on windows). Commissioned against
a real Nest Hub 2.

- **Bridge (`bridge/`)**: full Matter device model behind the adapter
  boundary; actions dispatch from OnOff **commands** (ADR-008 — repeats
  never drop, every tile tap fires), protocol v2 with custom commands,
  structured diagnostics with session observability, esbuild single-file
  bundle (one node process, ~0.9 s cold start). 328 tests, pure modules
  gated at 90 %+.
- **Tray app (`app/MatterHelm`)**: supervisor + loopback IPC, CoreAudio/
  media-key/key-chord/system-command/display executor, command macros with
  a non-blocking background runner, click-through overlay HUD with volume
  fill bar, settings window (categorized nav, search, custom-command CRUD
  incl. key-sequence capture and macro steps), dark mode, DPI-safe at
  200 %, local metrics + privacy-hardened diagnostics export. 439 tests.
  Budgets measured and enforced (ADR-007).
- **Sprints 0–8 delivered and adversarially reviewed**, including Sprint 3
  packaging (`build.ps1` single dist folder, Node SEA sidecar, factory
  reset, user guide) and the Sprint 8 deep-review pass. Remaining: the full
  hardware E2E checklist (`docs/e2e-log.md`) against the packaged dist.
  Natural voice phrases: see `docs/routines.md`.

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

## Quick start

```bash
cd bridge && npm ci && npm run verify        # sidecar: lint + types + tests
# app:  dotnet build app/MatterHelm/MatterHelm.csproj -c Release
# dist: ./build.ps1  → single folder with both executables
```

Requires Node 22 LTS (dev) — end users get a single-exe sidecar (Node SEA)
bundled next to the tray app.

## Contributing

MatterHelm is currently a private, solo-maintained repository. See
[`CONTRIBUTING.md`](CONTRIBUTING.md) for the build/test workflow, the
engineering bar, commit conventions, and the protocol-parity and ADR rules
that govern changes here.

## Security

Please report suspected vulnerabilities privately per
[`SECURITY.md`](SECURITY.md) (GitHub Security Advisories) rather than in a
public issue.

## License

MIT — see [`LICENSE`](LICENSE). "Matter" and the Matter certification mark
are trademarks of the Connectivity Standards Alliance; see
[`NOTICE`](NOTICE) for the trademark note ahead of any public release.
