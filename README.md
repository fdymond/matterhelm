# MatterHelm

[![CI](https://github.com/fdymond/matterhelm/actions/workflows/ci.yml/badge.svg)](https://github.com/fdymond/matterhelm/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/fdymond/matterhelm)](https://github.com/fdymond/matterhelm/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**Turn your Windows HTPC into a locally-paired Google Home device.** Say
"Hey Google, set HTPC volume to 40 %", tap tiles in the Home app, or wire
routines ("movie time") — MatterHelm executes the media, volume, power, and
custom actions directly on the PC.

No cloud, no OAuth server, no paid certification: pairing is a one-time
QR-code scan — Matter over your local network via a
[matter.js](https://github.com/matter-js/matter.js) virtual bridge, executed
natively by a tiny Windows tray app. Fully self-contained (`docs/adr/001`):
no external services, no companion apps.

```
Google Home app / Nest speaker
        │  Matter (local network)
        ▼
bridge/  Node 22 + matter.js sidecar      (Matter Aggregator: Speaker + switches)
        │  localhost WebSocket, token-auth
        ▼
app/     C# .NET 10 WinForms tray app     (supervisor + executor + UI)
        ├─ ActionExecutor → media keys / CoreAudio / key chords / system commands
        └─ Overlay HUD    → click-through pop-ups: incoming command + result
```

## Features

- **Voice + app + routines** — volume/mute with a real slider, play/pause,
  next/previous, configurable power off (displays off / sleep / pause-then-off).
- **Custom commands** — each becomes its own Google Home device: press a
  media key, launch a program, send a keyboard chord (typed or captured),
  run a system command (screensaver, lock, sleep, hibernate, shutdown…), or
  chain them into a **macro** with waits.
- **Robust command handling** — actions dispatch from Matter *commands*, so
  repeated voice commands and every tile tap fire (no dropped taps); long
  macros never block other commands.
- **Overlay HUD** — click-through, non-activating pop-ups for each command
  with a volume fill bar; 8 screen positions, light/dark/system theme,
  adjustable opacity.
- **Settings UI** — categorized pages with search, staged edits, inline
  validation, live preview.
- **Local-first diagnostics** — structured logs, metrics snapshots with
  resource gauges, one-click privacy-scrubbed diagnostics export. Nothing
  ever leaves the machine.
- **Lean** — measured budgets, enforced: tray ≈ 15 MB private, one sidecar
  process ≈ 95 MB, idle CPU < 0.5 % (see `docs/adr/007`).

## Requirements

1. **Windows 11** (x64).
2. A **Google Nest hub device** on the same LAN — Nest Hub/Mini/Audio, Nest
   Wifi Pro, or Google TV Streamer. A phone alone cannot commission Matter
   devices (`docs/adr/002`).
3. **IPv6 enabled** on the active network adapter (Matter requirement, even
   though everything stays local).
4. A one-time, **free** [Google Home Developer Console](https://console.home.google.com/)
   registration of the test VID/PID — step-by-step in the
   [user guide](docs/user-guide.md#one-time-google-home-developer-console-setup).

## Install

Download from [Releases](https://github.com/fdymond/matterhelm/releases/latest):

- **Installer** — `MatterHelm-Setup-<version>.exe`: installs to Program
  Files, Start-menu entry, optional start-with-Windows, clean uninstall.
- **Portable** — `matterhelm-<version>-win-x64.zip`: unzip anywhere and run
  `MatterHelm.exe`. Nothing else to install — the Matter sidecar ships as a
  bundled single exe; no Node.js or .NET runtime needed.

Verify downloads against `SHA256SUMS.txt` attached to each release:
`certutil -hashfile MatterHelm-Setup-<version>.exe SHA256`.

> **Note**: binaries are currently unsigned — SmartScreen may prompt on
> first run ("More info" → "Run anyway"). Windows Firewall will ask to allow
> the bridge on **Private** networks; that's required for pairing.

Then follow the [user guide](docs/user-guide.md): enable the bridge from the
tray, scan the pairing QR with the Google Home app, name your devices.

## Documentation

| Doc | What's in it |
|---|---|
| [User guide](docs/user-guide.md) | Install → console setup → pairing → settings tour → troubleshooting → privacy |
| [Natural voice phrases](docs/routines.md) | Routine starters ("pause the HTPC") and stateless button tiles |
| [Architecture blueprint](docs/BLUEPRINT.md) | Binding design & IPC protocol spec |
| [ADRs](docs/adr/) | Every architectural decision, with context and consequences |
| [Engineering standards](docs/ENGINEERING-STANDARDS.md) | The quality bar (TS + C#) |
| [Research](docs/RESEARCH.md) | Why this integration route (July 2026 survey) |

## Building from source

```bash
cd bridge && npm ci && npm run verify        # sidecar: lint + types + 328 tests
dotnet test app/MatterHelm.Tests/MatterHelm.Tests.csproj -c Release   # 463 tests
./build.ps1                                  # dist/: portable folder, SEA sidecar
```

Dev requirements: Node 22 LTS, .NET 10 SDK, Windows. CI runs the same gates
on every push (bridge verify + coverage on ubuntu/windows, app build + tests
+ coverage on windows).

## Status

**v0.2.0 released; Sprint 9 (settings/overlay polish, launch prep) on
`main`.** Commissioned and exercised against real Nest hub hardware.
791 automated tests across both processes, measured resource budgets
(`docs/adr/007`), and a scripted hardware E2E checklist
(`docs/e2e-log.md`). See [`CHANGELOG.md`](CHANGELOG.md) for history and
[`BACKLOG.md`](BACKLOG.md) for what's next.

## Contributing

Issues and PRs welcome — see [`CONTRIBUTING.md`](CONTRIBUTING.md) for the
build/test workflow, engineering bar, commit conventions, and the
protocol-parity and ADR rules that govern changes. Questions → 
[`SUPPORT.md`](SUPPORT.md).

## Security

Report suspected vulnerabilities privately per [`SECURITY.md`](SECURITY.md)
(GitHub Security Advisories), not in a public issue.

## License & trademarks

MIT — see [`LICENSE`](LICENSE). "Matter" and the Matter certification mark
are trademarks of the Connectivity Standards Alliance (CSA); see
[`NOTICE`](NOTICE). MatterHelm is an independent project, not affiliated
with or endorsed by the CSA or Google.
