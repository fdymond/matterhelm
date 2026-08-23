# MatterHelm user guide

MatterHelm turns your Windows HTPC into a **locally-paired Google Home
device**. Once it's set up you can say "Hey Google, set HTPC volume to
40 %", tap the devices in the Google Home app, or build routines like
"movie time" — and the app carries out the volume/media/power action
directly on your PC. Nothing about this involves the cloud beyond Google's
own Home app/hub: pairing is a one-time local QR-code scan, the same way you'd
pair any other Matter device.

This guide is written for someone setting the app up for the first time, not
a developer. If you hit something this guide doesn't cover, see
[Troubleshooting](#troubleshooting) or the technical docs linked from the
repository root.

## What MatterHelm is (and isn't)

- It is a small always-on tray app that stays running in your Windows
  notification area and:
  - runs a tiny local **bridge** that speaks the Matter protocol on your
    home network, so Google Home sees your PC as up to six devices (a
    speaker plus a handful of switches);
  - **executes every command itself** on the PC — there's no cloud service
    in the middle translating "pause" into a keypress; the app does that
    directly with Windows APIs (media keys, system volume, display power).
- It is **not** a voice assistant — it doesn't listen for or recognize
  speech itself. All the "Hey Google, …" recognition happens on your phone
  or Nest speaker, same as any other smart-home command; MatterHelm just
  receives the resulting Matter command.
- It is **not** a media player, and doesn't talk to any specific app (Kodi,
  a browser, etc.) — it presses the same play/pause/volume/next controls
  Windows already exposes to your keyboard's media keys, so it works with
  whatever is currently playing.

## Prerequisites

Before you install anything, make sure you have:

1. **Windows 11** on the PC that will run MatterHelm (the HTPC itself).
2. A **Google Nest hub device** — a Nest Hub, Nest Mini, Nest Audio, Nest
   Wifi Pro, or Google TV Streamer — on the **same Wi-Fi/LAN** as the PC.
   A phone alone is not enough: Google requires a hub device to act as the
   Matter "border" for locally-commissioned devices like this one.
3. **IPv6 enabled** on the PC's active network adapter. Matter requires it
   even though everything stays on your local network — nothing needs to
   reach the wider internet over IPv6. Check: Settings → Network & internet
   → your adapter → Edit IP assignment (or run `ipconfig` and confirm the
   adapter shows a "Link-local IPv6 Address"). It's on by default on nearly
   all Windows installs; you only need to check this if pairing fails later.
4. The **Google Home app** on your phone, signed into the account that owns
   your home, with Bluetooth and local-network permissions granted to it.
5. A one-time, **free** Google Home Developer Console registration (below) —
   this is not a paid or certified-manufacturer program, just a small form
   that tells Google "this test device is safe to pair."

### One-time Google Home Developer Console setup

MatterHelm isn't a commercially certified Matter product (that program costs
real money and is aimed at manufacturers shipping thousands of units), so
Google Home will refuse to pair it unless *your own* Google account has
registered its identifiers in a free developer project first. This takes
about five minutes and only needs to be done once per Google account.

1. In a browser, go to <https://console.home.google.com/> and sign in with
   the **same Google account your phone's Home app uses**.
2. Click **Create a project** (or **Get started** → new project) and give it
   any name you like, e.g. "My HTPC Bridge". *(Google's console UI moves
   around occasionally — if a step doesn't match exactly, look for the
   nearest equivalent option.)*
3. Inside the project, choose **Add integration** → **Matter**.
4. Fill in the Matter integration form:
   - **Vendor ID (VID)**: choose the **test VID**, `0xFFF1`.
   - **Product ID (PID)**: `0x8000`.
   - **Device type**: pick the bridge/aggregator option if offered (any
     option works — Google gates pairing on the VID/PID, not this field).
   - Leave it as a development/test integration — you do **not** need to
     submit it for certification or pay anything.
5. Save the integration. A draft integration is enough; there's no
   "publish"/"launch" step to complete.
6. If you picked different values (or already use `0xFFF1`/`0x8000` for
   another test device), set the matching pair in MatterHelm: Settings →
   Advanced → **Vendor ID (VID)** / **Product ID (PID)**. They accept hex
   (`0x8003`) or decimal. Changing them after pairing re-pairs the bridge.
7. Confirm the phone's Google account is the **owner** (or a member) of this
   project — commissioning only works for accounts in the project that
   registered the VID/PID.

If a step doesn't match what's on screen, look for wording like "test
device", "unlisted", or "development" — Google occasionally renames these
options.

## Installing

1. Download from the [releases page](https://github.com/fdymond/matterhelm/releases/latest) — either:
   - **Installer** (`MatterHelm-Setup-<version>.exe`): run it — no
     administrator prompt (it installs per-user), with an optional
     start-with-Windows checkbox, a Start-menu entry, and a clean uninstall
     that keeps your pairing and settings.
   - **Portable zip** (`matterhelm-<version>-win-x64.zip`): unzip anywhere
     (e.g. `C:\Apps\MatterHelm\`). The folder contains `MatterHelm.exe` plus
     a `sidecar\` folder — keep them together.

   Both are self-contained — no Node.js or .NET runtime needed. You can
   verify a download against the release's `SHA256SUMS.txt`:
   `certutil -hashfile <file> SHA256`.
2. Run `MatterHelm.exe` (the installer offers to). The helm icon
   appears in the system tray (the hidden-icons area near the clock) —
   that's the whole UI surface.
3. **Windows Firewall will likely prompt** the first time the bridge starts
   ("Windows Defender Firewall has blocked some features of this app").
   Click **Allow access** for **Private networks** (you don't need Public/
   Domain). This lets the bridge advertise itself and talk Matter to your
   Nest hub over the LAN — without it, pairing can't find the device.
4. Optional (portable zip only): to start MatterHelm with Windows, add a
   shortcut to `MatterHelm.exe` to your Startup folder (Win+R →
   `shell:startup`). The installer offers this as a checkbox instead.

## Enabling the bridge and pairing

1. Right-click the tray icon and check **Enable bridge**. The icon turns
   amber ("running, not yet paired") within a few seconds. If it turns red,
   see [Troubleshooting](#troubleshooting).
2. Click **Pair with Google Home…** in the tray menu. A window opens with a
   QR code and, below it, an 11-digit manual pairing code (use the manual
   code if the QR code won't scan, e.g. photographed off a low-quality
   screen).
3. On your phone, open the **Google Home** app → **+ Add** → **Matter-
   enabled device** (wording varies: "New device" → pick your home →
   "Matter device"/scan option).
4. Scan the QR code, or choose "Set up without QR code" and type the manual
   code instead.
5. Expect a screen saying something like **"This device isn't
   Matter-certified"** — this is expected for a self-hosted device like this
   one; tap through it. (A hard **"Not a Matter-certified device"** failure
   instead means the developer-console step above didn't take — see
   Troubleshooting.)
6. The Home app will ask which home/room and let you name the devices. You
   should see multiple new tiles appear — by default: **HTPC Speaker**,
   **HTPC Play Pause**, **HTPC Next**, **HTPC Previous**, and **HTPC
   Power**, plus one tile per custom command you've configured. (The name
   prefix "HTPC" and every individual name are yours to change any time in
   Settings → Devices & Commands, before or after pairing — the Google Home
   device name is whatever the app is configured to send at the time of
   pairing.)
7. Once paired, the tray icon turns **green**. Pairing itself never needs
   repeating unless you factory-reset (below) or remove the devices in the
   Home app.

## What each device does

| Google Home device | What it does on the PC |
|---|---|
| **HTPC Speaker** | System volume (voice "set … volume to 40 %" or the app's slider) and mute (voice "mute …" or the tile's power button) |
| **HTPC Play Pause** | Toggles play/pause on whatever is currently playing (same as your keyboard's media key) |
| **HTPC Next** / **HTPC Previous** | Skip to next/previous track |
| **HTPC Power** | Configurable in Settings: turn off the displays, sleep the PC, or pause playback then turn off the displays |
| Any custom command you've added | Whatever you configured it to do — see below |

The transport controls (Play Pause/Next/Previous) and Power show up as
switches that flip briefly to "on" and snap back — that's expected (Google
doesn't currently expose a plain "button" concept for locally-paired
devices with a direct voice target), it isn't a bug. How long the tile
stays "on" is the **Tap reset delay** setting (Devices & Commands); the
default is 0 — snap back immediately — and it's purely cosmetic either
way, the command always fires. Raise it if you prefer seeing the tile
light up briefly.

Because these commands are stateless buttons, **any** on/off command fires
them: tapping the tile always works no matter which state the Home app
happens to display (Google's shown state can lag the bridge), and saying
"turn **off** HTPC Next" presses it just like "turn on" would. Only HTPC
Power keeps distinct on/off meanings.

**Shown as a plug/outlet instead of a switch?** On the Matter wire these
devices are On/Off Plug-in Units (the only certified type Google both
voice-targets and lets a bridge control), so the Home app defaults their
icon/category to "Outlet". Google supports re-typing them per device: open
the device's tile → gear icon → **Type** (under Device information) →
choose **Switch**. This is Home-app metadata only — voice targets, routines
and behavior are unchanged, no re-pairing — but you'll need to redo it if
you ever factory-reset and re-pair.

### Natural voice phrasing

Saying "turn on HTPC Next" works, but doesn't feel natural. Google Home
supports **routines with custom starter phrases** that map any phrase you
like ("skip this track") onto turning one of these devices on. Setting that
up takes about five minutes and is entirely optional — see
[`docs/routines.md`](routines.md) for the recommended starter set and why
bare words like "pause"/"stop" can't be used directly (they collide with
Google's own global commands).

## Settings tour

Right-click the tray icon → **Settings…** opens a single window with a
search box and a left-hand list of categories. Changes are staged until you
click **Save** (closing the window with unsaved changes asks first).

- **General** — the bridge's IPC port (only matters if 39531 collides with
  something else on your PC), the sidecar's log detail level, and this
  app's own log detail level (applies immediately, no restart).
- **Devices & Commands** — rename or disable any of the five built-in
  devices, choose what the Power device does, tune how quickly a tapped
  command's switch snaps back to "off" in Google Home (default 0 =
  immediately; purely cosmetic), and manage **custom commands**:
  - **Add…** creates a new command, which becomes its own Google Home
    device once you save and re-pair (new devices need a config reload of
    the bridge, which happens automatically the next time it starts).
  - Each custom command needs a unique key (used internally, not shown to
    Google) and one action:
    - **Media key** — one of play/pause (toggle), dedicated play, dedicated
      pause (absolute — "play" never pauses and vice versa), next, previous,
      stop, mute, volume up, or volume down (the same ones the built-ins
      use, if you want a second speaker/transport device under a different
      name).
    - **Launch** — starts a program (e.g. your media center's exe) with
      optional arguments. The program's path must exist on disk when you
      save.
    - **Key sequence** — sends an arbitrary keyboard chord (e.g.
      `Ctrl+Shift+V`, or a single key like `F11`) to whatever window has
      focus. Click into the sequence field and press the keys you want —
      the dialog captures them and shows the resulting chord text so you
      can confirm it before saving.
    - **System command** — one functional Windows action: start/stop the
      screensaver, displays off/on, sleep, hibernate, lock the PC, close
      the focused program (a graceful close, like the title-bar X — apps
      may still prompt to save), shut down, or restart. Shut down and
      restart act immediately — no confirmation on the PC — so consider
      keeping those out of easily-tapped tiles.
    - **Command sequence (macro)** — runs several of the above in order
      from one voice command or tile tap. Build the step list with **Add…**
      (each step is a media key, launch, key sequence, system command, or a **Wait** of
      1–5000 ms for pacing between steps; up to 16 steps, waits summing to
      at most 10 s), reorder with Up/Down, and double-click a step to edit
      it. If a step fails, the macro stops there and the log names the
      failing step. A macro containing waits runs in the background so it
      never delays other commands — the overlay shows "running N steps"
      when it starts and the outcome when it finishes. Example — "movie
      time": launch Kodi → wait 2000 ms → `F11` for fullscreen.
  - Uncheck a command's box to keep it configured but stop publishing it to
    Google Home (its tile disappears from Home the next time the bridge
    restarts).
- **Overlay** — the small on-screen pop-up that flashes briefly whenever a
  command arrives ("Google Home → Volume" with a fill bar, or a pill like
  "play/pause pressed"). Toggle it, choose which screen corner/edge it
  appears at, pick its theme (follows the Windows light/dark setting by
  default, or force light/dark), set its opacity (100 = solid, lower =
  see-through), and use **Preview** to see a sample without waiting for a
  real command.
- **Advanced**:
  - **mDNS network interface** — leave blank unless your PC has more than
    one active network adapter (e.g. Wi-Fi *and* Ethernet, or a VPN/virtual
    adapter) and pairing can't find the device; pin it to your real LAN
    adapter's name (from `ipconfig`) in that case.
  - **Matter storage** — read-only, shows where the pairing data lives (see
    [Factory reset](#factory-reset--re-pairing)).
  - **Config file** / **Config folder** — opens `config.json` or its folder
    directly, for anyone who wants to hand-edit it (the app also does this
    safely through the UI).
  - **Export diagnostics…** — saves a zip of logs, metrics, a small system
    manifest (OS/.NET/Node versions — no username or machine name), and
    your config, for troubleshooting. Nothing is uploaded anywhere; hand
    the zip to whoever's helping you debug an issue. Your pairing
    credentials are never included.
  - **Reload config** — re-reads `config.json` from disk, discarding any
    unsaved edits in the window.
  - **Factory reset** — see the next section.

## Factory reset / re-pairing

If you need to start pairing over — a new phone, a botched setup, moving
the PC to a different Google home, or just wanting a clean slate — use
**Factory reset**, available two places: the tray menu ("Factory reset
bridge…", right under "Pair with Google Home…") and Settings → Advanced →
**Factory reset**. Both ask you to confirm first, since this is
destructive:

- The bridge stops immediately.
- The Matter pairing data (your PC's identity as far as Google Home is
  concerned) is **permanently deleted** from disk.
- Every MatterHelm device will show as **offline** in the Google Home app —
  Google doesn't learn about the reset on its own. Remove the offline
  tiles from the Home app yourself once you see them go gray (Home app →
  tap the device → Settings gear → Remove device).
- You'll need to **re-pair from scratch** (a fresh QR code) to use the
  devices again — repeat [Enabling the bridge and pairing](#enabling-the-bridge-and-pairing).
- Your **config and logs are kept** — device names, custom commands,
  overlay settings, and everything else in Settings survives a factory
  reset untouched; only the Google pairing itself is wiped.

If the bridge was running before the reset, it automatically restarts
afterward (ready for a fresh pairing); if it was already off, it's left
off. Watch the tray tooltip/log or the on-screen overlay for "factory reset
complete — open Pair with Google Home to re-pair" once it's done. If the
reset fails (rare — usually something briefly holding the storage folder
open, like antivirus scanning it right after the bridge stops), nothing is
deleted and the failure is logged; just try again a few seconds later.

## Running MatterHelm on more than one PC

Each PC pairs as its own set of Google Home devices, and nothing needs to be
shared between them:

- **Identity is per install.** The first time a fresh install starts, it
  mints its own device identity (Settings → Advanced → *Device identity
  seed* shows it), so two PCs never collide in the same home. An install
  that was already paired before this feature existed keeps its original
  identity — upgrading never unpairs you.
- **Give the second PC its own Product ID.** In Settings → Advanced set
  **Product ID (PID)** to another value from the test range
  `0x8000`–`0x801F` (e.g. `0x8001`), and register that pair in your
  Developer Console project alongside the first.
- **Give the devices distinct names** (Settings → Devices) — e.g. "Office
  PC Speaker" vs "HTPC Speaker" — otherwise voice commands are ambiguous
  even though the devices are distinct.

The same Google account and the same Developer Console project cover as many
PCs as you like; only the PID and the device names need to differ.

## Troubleshooting

- **A device shows offline in Google Home, but the tray icon is green.**
  This is a known Google-side behavior, not a bug in MatterHelm: after the
  bridge restarts (PC reboot, app update, or just Windows deciding to), the
  Nest hub can be very slow — or in the worst case, never — to reconnect to
  the same device on its own, even though the bridge is announcing itself
  correctly the whole time and nothing has actually changed on the pairing
  side. **Fix**: power-cycle the Nest hub (unplug 10 seconds, plug back in)
  or open the device's tile in the Home app and toggle it — either usually
  reconnects it within a minute. This is documented as a known risk in
  `docs/adr/006-telemetry-and-diagnostics.md` §4; no bridge-side setting
  changes it, since the stall is controller-side re-association policy, not
  a subscription the bridge can nudge.
- **Firewall / "can't find device" during pairing.** Make sure you clicked
  **Allow** on the Windows Firewall prompt (see Installing, step 3) for
  **Private networks**. If you dismissed it or picked "Cancel", delete the
  blocked "Node.js"/"MatterHelm" entries under Windows Security → Firewall
  → Allow an app, then restart the bridge (toggle **Enable bridge** off and
  on) to re-trigger the prompt.
- **Pairing fails with "Not a Matter-certified device" as a hard error**
  (not just a warning screen you can continue past). The Developer Console
  VID/PID registration (see Prerequisites) either wasn't completed, doesn't
  match `0xFFF1`/`0x8000` exactly, or the phone's Google account isn't a
  member of that project. Re-check that section; changes there can take a
  few minutes to propagate.
- **Pairing times out immediately, or matter.js errors mention IPv6.**
  IPv6 is disabled on your network adapter — re-enable it (adapter
  Properties → check "Internet Protocol Version 6 (TCP/IPv6)").
- **Multiple network adapters** (Wi-Fi + Ethernet, a VPN, Hyper-V/VMware
  virtual adapters). The bridge may be advertising itself on the wrong one.
  Set Settings → Advanced → **mDNS network interface** to your real LAN
  adapter's name (from `ipconfig`) and re-enable the bridge.
- **The tray icon is red.** The sidecar process is crash-looping (two or
  more restarts without successfully reconnecting). Check the app log
  (Settings → Advanced → Config folder → `logs\`) for the actual error, or
  export diagnostics and take a look — a common cause is another program
  already using the configured IPC port (change it in Settings → General).
- **Need a clean slate.** Use [Factory reset](#factory-reset--re-pairing).

## Privacy notes

- MatterHelm makes **no network calls beyond your own LAN** (Matter/mDNS to
  your hub) and never talks to any MatterHelm-operated server — there isn't
  one. Google's own Home/Assistant services are, of course, involved the
  same way they are for any Google Home device; that's between you and
  Google, not this app.
- The **diagnostics export** (Settings → Advanced → Export diagnostics…)
  never uploads anything — it just saves a zip to a location you choose.
  It intentionally excludes anything that could identify your PC (no
  username, no machine name, no file paths besides what you've typed into
  your own config, e.g. a custom command's launch path) and scrubs any
  commissioning codes that might otherwise have been logged. Your live
  session's pairing token is never written to disk or logged in the first
  place, so it can't leak into a bundle.
- `config.json` (included verbatim in a diagnostics export) is exactly what
  you see in Settings — nothing hidden gets added to it.
