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
        ├─ ActionExecutor → SMTC + media keys / CoreAudio / key chords / system commands
        └─ Overlay HUD    → click-through pop-ups: incoming command + result
```

## Features

- **Voice + app + routines** — volume/mute with a real slider; retained
  Play/Pause, Next, and Previous switches; and configurable Power behavior:
  displays off/on, pause then displays off/on (without resuming playback),
  screensaver start/stop, or momentary sleep.
- **Custom commands** — each becomes its own retained Google Home switch by
  default, with an opt-in **Reset after activation** one-shot mode: press a
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

## Setup from scratch

Start to finish this takes about 15 minutes, most of it the one-time Google
registration. The [user guide](docs/user-guide.md) covers every step in more
detail plus troubleshooting.

### 1. Check the requirements

| Need | Why |
|---|---|
| **Windows 10 version 1809 (build 17763) or later**, x64 | The current app target is `net10.0-windows10.0.17763.0`; Windows 10 and 11 are supported |
| A **Google Nest hub** on the same LAN — Nest Hub/Mini/Audio, Nest Wifi Pro, or Google TV Streamer | Google requires a hub to commission Matter devices; a phone alone cannot (`docs/adr/002`) |
| **IPv6 enabled** on the active network adapter | A hard Matter requirement, even though all traffic stays local. It's on by default — only check this if pairing later fails |
| The **Google Home app**, signed into the account that owns your home | Does the pairing scan |

### 2. Register the test IDs (one-time, free)

MatterHelm isn't a commercially certified Matter product, so Google will only
pair it if *your own* account has registered its identifiers first. Free, no
review, ~5 minutes, once per Google account.

1. Go to [console.home.google.com](https://console.home.google.com/) and sign
   in with **the same account your Home app uses**.
2. **Create a project** (any name).
3. **Add integration → Matter**.
4. Enter the sanctioned test IDs: **VID `0xFFF1`**, **PID `0x8000`**. Pick the
   bridge/aggregator device type if offered — Google gates pairing on the
   VID/PID, not that field. Leave it as a development/test integration; don't
   submit for certification.
5. Save as a draft integration.

> Google's console UI moves around. If wording doesn't match, look for "test
> device", "unlisted", or "development".

### 3. Install

Download from [Releases](https://github.com/fdymond/matterhelm/releases/latest):

- **Installer** — `MatterHelm-Setup-<version>.exe`: per-user install (no
  admin prompt), Start-menu entry, optional start-with-Windows, clean
  uninstall that keeps your pairing and settings.
- **Portable** — `matterhelm-v<version>-win-x64.zip`: unzip anywhere and run
  `MatterHelm.exe`.

Either way it's self-contained — the Matter sidecar ships as a bundled single
exe, so **no Node.js or .NET runtime is required**. Optionally verify the
download against the release's `SHA256SUMS.txt`:

```
certutil -hashfile MatterHelm-Setup-<version>.exe SHA256
```

> **Unsigned binaries**: SmartScreen may warn on first run — "More info" →
> "Run anyway". Code-signing is on the roadmap.

Run it: a **helm icon** appears in the system tray (check the hidden-icons
area). That's the entire UI. On a fresh install a **setup guide** opens with
the remaining steps and a button that does them for you — steps 4 and 5 below
are the same thing done by hand. (Tray menu → **Setup guide…** reopens it.)

### 4. Name your devices before pairing

Right-click the tray icon → **Settings** → **Devices**. The names here become
your voice targets and are what the Home app offers during pairing, so set
them now — especially if more than one PC will run MatterHelm (see below).
Untick anything you don't want published.

### 5. Start the bridge and pair

1. Right-click the tray icon → **Pair with Google Home…**. On an unpaired
   install this is the bridge-start action: it enables and persists the bridge
   and opens the pairing window. **Enable bridge** is intentionally hidden
   until pairing has completed.
2. The icon is briefly **amber** while starting, then **blue** when the bridge
   is healthy, advertising, and awaiting pairing. **Allow the Windows Firewall
   prompt** for **Private** networks. Without it
   the hub can't discover the bridge.
3. The pairing window shows the phone-side steps, a QR code, an 11-digit manual
   code, and a live status line that follows the bridge through to paired.
4. In the Home app: **+ Add** → **Matter-enabled device**, scan the QR (or
   "Set up without QR code" and type the manual code).
5. Tap through the **"not Matter-certified"** notice — expected for a
   self-hosted device. A hard *"Not a Matter-certified device"* failure
   instead means step 2 didn't take.
6. Pick a home/room and confirm the device names. The tray icon turns
   **green**.

Tray legend: **gray** = disabled; **amber** = starting or awaiting lifecycle
status; **blue** = healthy and awaiting pairing; **green** = commissioned and
connected; **red** = a sidecar crash loop or missing commissionable
advertisement.

You'll get tiles for **HTPC Speaker**, **HTPC Play Pause**, **HTPC Next**,
**HTPC Previous**, **HTPC Power**, plus one per custom command.

### 6. Try it

> "Hey Google, set HTPC Speaker volume to 40 %"
> "Hey Google, turn on HTPC Play Pause"

Play/Pause On requests Play and Off requests Pause in the focused program
first. MatterHelm verifies or falls back through a Windows media session only
when its owner matches the focused app; a different app's stale session cannot
suppress or prove the command. If focused delivery fails, the current session
becomes the absolute fallback target. Sessionless players such as Kodi remain
usable but explicitly unverifiable. Next and
Previous retain their displayed state and fire once on either user transition.

For natural phrasing like *"pause the HTPC"*, set up Google Home routines —
see [docs/routines.md](docs/routines.md).

## Multiple PCs in one home

Each PC pairs separately and appears as its own set of devices. You need
**no** second Google account, second Console project, second hub, or
different Vendor/Product IDs — every install mints its own Matter identity on
first run, which is what keeps them distinct.

The one thing that needs your attention is **names**, since every install
ships the same defaults and duplicate names make voice commands ambiguous.

On the second (third, …) PC:

1. Install and run it. A fresh unpaired install remains disabled until you
   choose **Pair with Google Home…**.
2. Settings → **Devices** → set **Bridge name** (e.g. "Office Bridge" — what
   Google calls the bridge itself) and rename each device: "Office Speaker",
   "Office Play Pause", "Office Power", … Save.
3. *(Optional check)* Settings → **Advanced** → **Device identity seed**
   should differ from the other PC's.
4. Choose **Pair with Google Home…** and allow the **firewall** prompt — it's
   a fresh prompt on this PC.
5. Complete pairing as in step 5 above, using the **same Google account and home**. Put
   it in a different **room** if you can; it makes voice targeting easier.

Then both respond independently: *"pause the office PC"* vs *"pause the
HTPC"*.

**Cloned machines**: if the second PC was made by imaging the first (or you
copied `%APPDATA%\MatterHelm\config.json` across), it inherits the first
PC's identity and the two will conflict. Fix: close MatterHelm on the clone,
delete the `"uniqueIdSeed"` line from `config.json` and the `matter` folder
beside it, then start it — a fresh identity is minted.

## Documentation

| Doc | What's in it |
|---|---|
| [User guide](docs/user-guide.md) | The setup above in more depth, plus the full settings tour, custom commands and macros, troubleshooting, and privacy |
| [Natural voice phrases](docs/routines.md) | Routine starters and retained/resettable switch guidance |
| [Architecture blueprint](docs/BLUEPRINT.md) | Binding design & IPC protocol spec |
| [ADRs](docs/adr/) | Every architectural decision, with context and consequences |
| [Engineering standards](docs/ENGINEERING-STANDARDS.md) | The quality bar (TS + C#) |
| [Research](docs/RESEARCH.md) | Why this integration route (July 2026 survey) |

## Building from source

```bash
cd bridge && npm ci && npm run verify        # sidecar: lint + types + 393 tests
dotnet test app/MatterHelm.Tests/MatterHelm.Tests.csproj -c Release   # 803 tests
./build.ps1                                  # dist/: portable folder, SEA sidecar
```

> **0.5.0 gate evidence** (integrator, standard commands, clean environment):
> bridge `npm run verify` 391/391 green (lint + typecheck + tests, zero
> warnings); app suite 720/720 green; `build.ps1` SEA packaging succeeds;
> overlay, pairing-window and wired demos PASS. Dedicated Play/Pause were
> additionally verified against a live Windows media session in the packaged
> build: absolute play with a session, honest failure without one.

Dev requirements: Node 22 LTS, .NET 10 SDK, Windows. CI runs the same gates
on every push (bridge verify + coverage on ubuntu/windows, app build + tests
+ coverage on windows).

## Status

**0.5.0 is unreleased.** Its documentation and hardware release gates are in
progress; see the changelog's `[Unreleased]` section and current E2E checklist.
The latest tagged release is **v0.4.4** (deep-review hardening of
lifecycle/memory paths, streamlined onboarding, filtered adapter dropdown,
honest display-power labelling; v0.4.3 before it: DDC/CI display power — no
more Modern-Standby sleep on "displays off" — single-pill overlay, mDNS
adapter dropdown; v0.4.2 before it: Windows mDNS discovery fix — the bridge now answers
mDNS queries instead of announcing into the void — tray auto-update,
advertisement health status, live pairing window with auto-close, blue
awaiting-pairing tray state, power-action fidelity with screensaver
support; v0.4.1 before it: first-run guide, rebuilt pairing window,
factory-reset re-pair fix). Commissioned and exercised against real
Nest hub hardware, re-paired end-to-end after the discovery fix. Automated
test inventory is 1,111 (391 bridge + 720 app), but clean 0.5.0 release-gate
results remain pending an integrator run outside this restricted sandbox.
Measured resource budgets are recorded in `docs/adr/007`, and the current
scripted hardware E2E checklist is `docs/e2e-log.md`. See
[`CHANGELOG.md`](CHANGELOG.md) for history and
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
