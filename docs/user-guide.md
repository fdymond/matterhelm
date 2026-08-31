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
    home network, so Google Home sees up to five enabled built-in devices
    plus one device for every enabled custom command;
  - **executes every command itself** on the PC — there's no cloud service
    in the middle translating "pause" into a keypress; the app does that
    directly with Windows APIs (media keys, system volume, display power).
- It is **not** a voice assistant — it doesn't listen for or recognize
  speech itself. All the "Hey Google, …" recognition happens on your phone
  or Nest speaker, same as any other smart-home command; MatterHelm just
  receives the resulting Matter command.
- It is **not** a media player. Play, Pause, and Play/Pause first address the
  focused program. MatterHelm uses Windows System Media Transport Controls
  (SMTC) only when the session owner matches that focused app, or as the target
  after focused delivery fails. A delivered command to an app with no session
  is explicitly unverifiable.

## Prerequisites

Before you install anything, make sure you have:

1. **Windows 10 version 1809 (build 17763) or later**, x64, on the PC that
   will run MatterHelm. The shipped target is
   `net10.0-windows10.0.17763.0`.
2. A **Google Nest hub device** — a Nest Hub, Nest Mini, Nest Audio, Nest
   Wifi Pro, or Google TV Streamer — on the **same Wi-Fi/LAN** as the PC.
   A phone alone is not enough: Google requires a hub device to act as the
   Matter "border" for locally-commissioned devices like this one.
3. **IPv6 enabled end to end.** Matter requires IPv6 on the network interface
   MatterHelm uses **and** on the LAN path between the phone/Nest hub and the
   PC. The router must pass local IPv6 traffic and Neighbor Discovery (ND)
   between them; IPv6 internet access is not required. On the PC, run
   `ipconfig` and confirm the selected adapter has a "Link-local IPv6
   Address". See [IPv6 and Neighbor Discovery](#ipv6-and-neighbor-discovery)
   if pairing produces only a generic timeout.
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
   - **Portable zip** (`matterhelm-v<version>-win-x64.zip`): unzip anywhere
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

## Updating

Right-click the tray icon and choose **Check for updates…**. MatterHelm checks
the latest GitHub release and tells you when you are already current; if a
newer version exists, it asks before downloading anything. Every download is
verified against `SHA256SUMS.txt` from that same release before it can run.

- An **installed** copy downloads the matching Inno Setup package, closes
  MatterHelm and its bridge cleanly, then starts the verified installer in
  silent mode. The installer shows progress and does not restart Windows.
- A **portable** copy downloads the matching zip, closes MatterHelm and its
  bridge, replaces the files in the current portable folder from a temporary
  helper, and relaunches `MatterHelm.exe`.

MatterHelm also checks quietly about one minute after startup and every 24
hours. A newer release produces one subtle tray notification; it is never
downloaded automatically. To disable background checks, set
`"updateCheckEnabled": false` in `config.json` and choose **Reload config**;
manual checks remain available.

Until the GitHub repository becomes public, release checks return
"unavailable" unless a tester starts MatterHelm with the optional
`MATTERHELM_UPDATE_TOKEN` environment variable set to a GitHub token that can
read the repository. The token is used only in memory for GitHub requests and
is never saved, logged, or displayed. Normal public releases require no token.

### Upgrading from 0.4.x

Version 0.5.0 changes how switch state maps to actions:

- Existing custom commands do not contain the new `resetAfterActivation`
  field, so they load as retained switches and execute on either user
  transition. Edit a command in **Settings → Custom devices** and check
  **Reset the switch after it runs (momentary button)** if it should execute
  only when turned On and then return to Off.
- **Play Pause** is now a retained state switch: On requests absolute Play and
  Off requests absolute Pause. It no longer auto-resets or treats both states
  as the same toggle request.
- **Power** now models awake state. Reversible modes use Off to engage and On
  to reverse; pause-plus-displays-off never resumes playback. Sleep fires once
  on Off and the tile promptly returns to On.
- Local IPC message revision moved to v4. The tray and sidecar ship together,
  so no user action is required. A stale sidecar left running from another
  copy is rejected and logs a version mismatch; exit the other copy and start
  the matching package.

## Enabling the bridge and pairing

> **The setup guide does this for you.** On a fresh install a **Welcome**
> window opens by itself with these steps and a **Start bridge & pair**
> button that performs the first step below in one click. It appears once; reopen
> it any time from the tray menu → **Setup guide…**. If you'd rather drive it
> manually, the steps are:

1. Right-click the tray icon and choose **Pair with Google Home…**. On an
   unpaired install this is the only bridge-start action: it enables the
   bridge, saves that choice for future launches, and opens the pairing
   window. The icon briefly shows amber while starting, then turns blue
   ("running, not paired yet") within a few seconds. If it turns red, see
   [Troubleshooting](#troubleshooting).
2. The condensed pairing window keeps the stage/status, QR code, selectable
   manual code, one advertisement/pairing status line, and the active VID/PID
   in view. Open **Setup requirements…** only when you need the phone path,
   Developer Console and hub-reboot reminder, IPv6 requirement, or
   per-install identity/cloned-machine notes.
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
   Settings → Devices, before or after pairing — the Google Home
   device name is whatever the app is configured to send at the time of
   pairing.)
7. Once paired, the tray icon turns **green**. Pairing itself never needs
   repeating unless you factory-reset (below) or remove the devices in the
   Home app.

## What each device does

| Google Home device | What it does on the PC |
|---|---|
| **HTPC Speaker** | System volume (voice "set … volume to 40 %" or the app's slider) and mute (voice "mute …" or the tile's power button) |
| **HTPC Play Pause** | A true state switch: On sends dedicated Play; Off sends dedicated Pause |
| **HTPC Next** / **HTPC Previous** | Skip to next/previous track on either switch transition |
| **HTPC Power** | A stateful awake/asleep toggle for reversible actions; irreversible actions use a momentary Off command (table below) |
| Any custom command you've added | Whatever you configured it to do — see below |

Play Pause, Next, Previous, and reversible Power modes keep the state you
select. Play Pause uses that state directly, so turning it On always requests
Play and turning it Off always requests Pause — it no
longer sends the toggle media key. Play, Pause, and Play/Pause first address
the focused program, which lets focus-driven players such as Kodi respond.
MatterHelm resolves the focused process/app identity and compares it with the
session's source-app identity before using session state. It waits 400 ms, then
checks only a session belonging to that same app. A different app's session
cannot suppress delivery, verify it, or receive fallback after the focused
command was delivered. If delivery to a specific focused window fails, only a
session owned by that same focused app is an eligible fallback; MatterHelm
fails honestly instead of sending Play/Pause to another app. A failed delivery
with no focused target may still use the captured current session. When the
focused window times out specifically, MatterHelm waits 200 ms and retries the
appcommand once; ordinary refusals are not retried. All session fallbacks are
absolute Play/Pause operations; Play/Pause never sends a second toggle.

For an unverifiable target, **success means only that Windows delivered the
appcommand to the focused window**. Google receives an OK and the overlay shows
success even if playback did not change; the log says `unverifiable`, never
verified. Kodi publishes no session and its measured Pause appcommand toggles.
MatterHelm suppresses an identical dedicated Play/Pause verb repeated to the
same unverifiable process within two seconds, but the same Pause after that
window (or after restart) can still resume Kodi. Next and Previous use the switch as a
two-sided trigger: each user transition fires once, so On→Off skips just as
Off→On does. Power retains state for reversible display/screensaver actions;
actions that take the PC and bridge offline promptly return the tile to On.

Custom commands also keep state and fire once on either user transition by
default. In a custom command's editor, enable **Reset the switch after it runs
(momentary button)** if you need a one-shot button instead. Then only On runs
the command, and the tile resets to Off after **Tap reset delay**; the
automatic reset never runs the command again.

The **Power off behavior** setting defines both halves of that toggle:

| Configured action | Power Off | Power On | Automatic reset |
|---|---|---|---|
| **Displays off** | Powers compatible displays off in hardware through DDC/CI, without telling Windows that the screens are off | Restores DDC/CI-managed displays, sends a harmless net-zero mouse nudge, and releases any fallback keep-awake hold | No; state is retained |
| **Pause, then displays off** | Sends dedicated Pause, then uses the same DDC/CI-first display handling | Restores the displays and releases any fallback hold; playback remains paused | No; state is retained |
| **Start screensaver** | Remembers the focused window, then starts the screensaver configured in Windows | Stops the running screensaver, then validates and restores the remembered window | No; state is retained |
| **Sleep** | Dispatches sleep once | No action | Yes; the tile is promptly written back to On without dispatching another action |

The Sleep reset is deliberately independent of **Tap reset delay**, which is
only for custom commands. MatterHelm queues the local On write immediately
after dispatching sleep so it has the best chance to publish the awake/default
state before Windows suspends the app. When the PC wakes later, another Power
Off command can therefore run sleep again.

Screensaver focus restoration is intentionally narrow. Immediately before
MatterHelm starts a screensaver - from the Power device or a custom **Start
screensaver** command - it remembers the foreground window handle, owning
process ID/name, and title in memory. After MatterHelm handles Power On or a
custom **Stop screensaver** command, it first stops the saver, verifies that
the handle is still a real window owned by the same process, restores it if it
is minimized, and asks Windows to make it foreground. If normal activation is
refused, MatterHelm retries while temporarily attached to the current
foreground thread's input queue. Immediately after a saver closes Windows may
briefly have no foreground input queue, so MatterHelm revalidates the captured
HWND/PID/process name and retries every 100 ms for up to 1.5 seconds. The log
records the target and successful route at INFO, or the final
validation/Windows-refusal reason at WARN. Dismissing the screensaver remains
successful even when this best-effort focus restore is refused, so a macro
continues to its next step. The capture is consumed after that bounded attempt,
replaced by a later start, and cleared when the bridge is disabled or
MatterHelm exits; it is never saved across restarts.

This does **not** run when you dismiss the screensaver yourself with the mouse
or keyboard: MatterHelm receives no stop command, so it cannot restore the
previous focus. Use the MatterHelm Power On or **Stop screensaver** action when
focus restoration matters. **Displays off** and **Pause, then displays off**
do not steal focus, so they deliberately neither capture nor restore a window.

For **Displays off**, MatterHelm first sends VESA DDC/CI power mode to every
physical monitor that accepts it. This switches the display hardware off while
Windows continues to see an active screen, so screen-off does not trigger S0
Modern Standby and media players keep running normally: this is a true
displays-only action. DDC/CI may need to be enabled in the monitor or TV's own
settings.

Laptop internal panels do not support DDC/CI. If no display accepts DDC/CI,
MatterHelm falls back to Windows' global display-blanking command and acquires
the legacy keep-awake hold. On a Modern Standby PC that hold cannot veto the
screen-off transition, so the PC will also enter standby and suspend media
apps. Choose the explicit **Sleep** action instead on such a machine; it states
the outcome honestly and asks Windows to sleep directly. The Settings selector
labels the display-off choices according to the displays detected when the
window opens, and the log identifies the path used on every transition.

On a mixed setup, such as a DDC/CI television plus a non-DDC laptop panel,
MatterHelm powers the compatible display off but deliberately leaves the
unsupported panel on. Windows blanking affects every display and would undo
the no-standby benefit for the whole PC. Power On restores only the displays
that MatterHelm powered off, then sends the harmless mouse nudge for any
Windows-blanked panel. Any fallback keep-awake hold is still released on Power
On, bridge disable, client loss/restart, configuration changes away from a
display mode, crash-loop handling, and app exit.

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

## The tray menu

Right-clicking the tray icon is the whole control surface:

| Item | What it does |
|---|---|
| **Pair with Google Home…** | Shown only while unpaired; implicitly enables/persists the bridge and opens the pairing window |
| **Enable bridge** | Shown only after commissioning; starts/stops the paired Matter sidecar and persists across restarts |
| **Factory reset bridge…** | Deletes the pairing data — see [Factory reset](#factory-reset--re-pairing) |
| **Overlay pop-ups** | Toggles the on-screen command HUD |
| **Settings…** | The settings window, below |
| **Reload config** | Re-reads `config.json` from disk |
| **Setup guide…** | Reopens the first-run walkthrough |
| **Check for updates…** | Checks GitHub and offers a verified tray-driven update when a newer release exists |
| **About** | Version and links |
| **Exit** | Stops the sidecar and quits |

The bridge-control items change live: commissioning hides **Pair with Google
Home…** and reveals **Enable bridge**; factory reset reverses that. **Factory
reset bridge…** and the global Settings, update, overlay, setup-guide, About,
reload, and Exit actions remain available in either state.

Hovering the icon shows the current state in words:

| Colour | Meaning |
|---|---|
| **Gray** | Bridge disabled |
| **Amber** | Bridge starting or waiting for sidecar status |
| **Blue** | Enabled and running but not commissioned; tooltip: "running, not paired yet" |
| **Green** | Paired and connected |
| **Red** | Sidecar crash/restart loop, or the pairing advertisement is not visible; check the log |

## Settings tour

Right-click the tray icon → **Settings…** opens a single window with a
search box and a left-hand list of categories. Changes are staged until you
click **Save** (closing the window with unsaved changes asks first).

- **General** — **Enable bridge**, the bridge's IPC port (only matters if
  39531 collides with something else on your PC), the sidecar's log detail
  level, and this app's own log detail level (applies immediately, no restart).
- **Devices** — edit the **Bridge name**, rename or disable any of the five
  built-in devices, choose whether Power uses displays off, pause then displays
  off, the Windows screensaver, or sleep, and set the reset delay used by custom
  commands that opt into momentary behavior (default 0 = immediately).
- **Custom devices** — manage custom commands:
  - **Add…** creates a new command, which becomes its own Google Home
    device once you save. Saving a topology-changing edit restarts an enabled
    bridge immediately; Google Home may still require factory reset/re-pairing
    before it discovers an added or removed endpoint.
  - Each custom command needs a unique key (used internally, not shown to
    Google) and one action. By default its switch retains state and either
    transition runs the action once. Check **Reset the switch after it runs
    (momentary button)** to make only On run it and automatically return the
    tile to Off; that automatic reset does not run the action again.
    - **Media key** — one of play/pause (toggle), dedicated play, dedicated
      pause, next, previous, stop, mute, volume up, or volume down. Play,
      Pause, and Play/Pause use the focused-first, verified-session-fallback
      route described above. This can expose another transport target under a
      different name.
    - **Launch** — starts a program (e.g. your media center's exe) with
      optional arguments. **Browse…** picks a normal program; **Store app…**
      lists apps installed from the Microsoft Store (Spotify, Media Player,
      …), which cannot be launched from their own install folder and need
      the shortcut Windows keeps for them. The path must exist when you save.
    - **Key sequence** — sends an arbitrary keyboard chord (e.g.
      `Ctrl+Shift+V`, or a single key like `F11`) to whatever window has
      focus. Click into the sequence field and press the keys you want —
      the dialog captures them and shows the resulting chord text so you
      can confirm it before saving.
    - **System command** — one functional Windows action: start/stop the
      screensaver (with the same start-capture/stop-restore behavior described
      above), displays off/on, sleep, hibernate, lock the PC, close
      the focused program (a graceful close, like the title-bar X — apps
      may still prompt to save), shut down, or restart. Shut down and
      restart act immediately — no confirmation on the PC — so consider
      keeping those out of easily-tapped tiles.
    - **Move the mouse** — moves to bottom right (the classic parked
      position), another virtual-desktop corner, the centre, or explicit X/Y
      coordinates. Presets use the complete multi-monitor virtual desktop;
      Windows clamps explicit coordinates into it, so the pointer cannot be
      parked off-screen (measured on a 1600×1000 desktop: `(5000,5000)` landed
      at `(1600,1000)` and `(-500,-500)` at `(0,0)`). A corner preset is the
      available non-invasive parking behavior; MatterHelm does not hide the
      cursor. As a standalone action this must remain a retained switch: On
      captures the current pointer position and moves it, while Off restores
      the position captured by the last On. Off before any On is a successful
      no-op. The captured position is in memory only and is lost when
      MatterHelm exits.
    - **Command sequence (macro)** — runs several of the above in order
      from one voice command or tile tap. Build the step list with **Add…**,
      then choose the step type from its dropdown. **Mouse move** uses the
      same target picker described above. Each
      step is a media key, launch, key sequence, system command, mouse move,
      or a **Wait** of 1–5000 ms for pacing between steps; up to 16 steps are
      allowed, with waits summing to at most 10 s. Reorder with Up/Down and
      double-click a step to edit it. A mouse step is a one-shot absolute move
      to its clamped target: it does not capture or restore a position and
      cannot read or change a standalone mouse command's retained restore
      state. To move back, add another mouse step with the desired target.
      If any step fails, the macro stops there and the log names the failing
      step. A macro containing waits runs in the background so it never delays
      other commands — the overlay shows "running N steps" when it starts and
      the outcome when it finishes. Example — "movie time": launch Kodi →
      wait 2000 ms → `F11` for fullscreen → move the pointer to the top left.
  - Uncheck a command's box to keep it configured but stop publishing it to
    Google Home (its tile disappears from Home the next time the bridge
    restarts).
- **Overlay** — the small on-screen pop-up that flashes briefly whenever a
  command arrives. Its header is always **MatterHelm**; below it is one
  command/result pill or a volume fill bar. Toggle it, choose which screen corner/edge it
  appears at, pick its theme (follows the Windows light/dark setting by
  default, or force light/dark), set its opacity (100 = solid, lower =
  see-through), and use **Preview** to see a sample without waiting for a
  real command.
- **Advanced**:
  - **mDNS network interface** — keep **Auto (recommended)** unless your PC
    has more than one active adapter (e.g. Wi-Fi *and* Ethernet, or a
    VPN/virtual adapter) and pairing can't find the device. The dropdown lists
    sensible LAN adapters by default with their IPv4 address (or **no IPv4**);
    select **Show all adapters** for unusual setups. A saved adapter that was
    unplugged or renamed is shown as **(not detected)**; choose Auto or a
    detected adapter to replace it.
  - **Matter storage** — read-only, shows where the pairing data lives (see
     [Factory reset](#factory-reset--re-pairing)).
  - **Vendor ID (VID)** / **Product ID (PID)** — the Matter identifiers that
    must match the Google Home Developer Console integration. Saving a change
    requires a fresh pairing.
  - **Device identity seed** — read-only stable endpoint identity for this
    install. It is shown for clone/collision troubleshooting.
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
**Factory reset**, available two places: the tray menu (in both paired and
unpaired states) and Settings → Advanced → **Factory reset**. Both ask you to
confirm first, since this is
destructive:

- The bridge stops, then **restarts by itself** so it is immediately
  discoverable again for pairing (even if it was switched off when you ran
  the reset — a reset exists only to re-pair, and a bridge that is not
  running advertises nothing for your phone to find).
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

**The pairing window opens by itself** once the reset finishes. It shows
"Advertisement starting" for a few seconds and then swaps itself to the new
code — wait for the code to appear before you scan. The old code stops
working the moment you confirm the reset, so a code you photographed or left
on screen beforehand will fail in the Home app with **"can't find device"**.
If the reset fails (rare — usually something briefly holding the storage folder
open, like antivirus scanning it right after the bridge stops), nothing is
deleted and the failure is logged; just try again a few seconds later.

## Running MatterHelm on more than one PC

Each PC pairs separately and appears as its own set of devices in Google
Home. **You do not need a second Google account, a second Developer Console
project, or a second hub** — and you do not need to change the Vendor/Product
IDs. Every install mints its own Matter identity on first run, which is what
keeps two PCs distinct.

The one thing that genuinely needs your attention is **names**: both PCs ship
the same defaults ("HTPC Speaker", "HTPC Play Pause", …), and two devices
with the same name make voice commands ambiguous. Rename *before* pairing —
the names MatterHelm is publishing at that moment are the ones the Home app
offers you during setup.

### Setting up the second PC

1. **Install** MatterHelm on the second PC (installer or portable zip) and
   run it. Leave the bridge disabled for now.
2. **Rename its devices**: Settings → **Devices** → give each one a name
   that says which PC it is — e.g. "Office Speaker", "Office Play Pause",
   "Office Power". Save.
3. *(Optional sanity check)* Settings → **Advanced** → **Device identity
   seed** should differ from the first PC's. If the two PCs somehow show the
   same seed — which can only happen if you cloned a disk image or copied
   `config.json` between them — see the note below.
4. Choose tray → **Pair with Google Home…**. This starts the bridge; allow the
   **Windows Firewall** prompt for **Private** networks. It is a fresh prompt
   on this PC even though you allowed it on the first one.
5. In the Home app on your phone choose **+ Add** → **Matter-enabled device**
   and scan the QR. Use the same Google account and home as the first PC.
6. Tap through the "not Matter-certified" screen, pick a **room** (a
   different room from the first PC makes voice targeting easier still),
   and confirm the device names.

That's it — both PCs now respond independently: *"Hey Google, pause the
office PC"* vs *"…pause the HTPC"*.

### Notes and edge cases

- **Cloned machines**: if the second PC was made by cloning the first
  (disk image, or copying `%APPDATA%\MatterHelm\config.json` across), it
  inherits the first PC's identity and the two will conflict. Fix: on the
  clone, close MatterHelm, delete the `"uniqueIdSeed"` line from
  `config.json`, delete the `matter` folder beside it, and start the app —
  it mints a fresh identity and can be paired as a new device.
- **Vendor/Product IDs stay the same on both.** Keeping them identical means
  no extra Developer Console work. Changing the PID on one PC (to another
  value in the test range `0x8000`–`0x801F`) is only worth doing if you hit
  a problem, and it requires registering that pair in your Console project
  too.
- **Requirements are per PC**: each needs IPv6 enabled on its adapter, the
  firewall allowance, and a working LAN path to the same Nest hub.

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
- **The pairing window shows no QR code.** It tells you which of the two
  reasons applies. *"Advertisement starting"* means there is no code yet —
  choose **Pair with Google Home…** if you have not already, and give it a
  few seconds; that action starts the bridge and the code appears on its own.
  *"Paired"* means this
  PC is already commissioned, and a second code can't be issued for it; to
  pair it again (or to a different home) run
  [Factory reset](#factory-reset--re-pairing) first.
- **"Can't find device" when re-pairing right after a factory reset.** The
  reset issues a NEW code and invalidates the old one immediately. Wait for
  the pairing window (which opens by itself) to leave "Advertisement starting"
  and show the new code before you scan — scanning the previous code sends
  your phone looking for a device that no longer exists. Also remove the
  now-offline MatterHelm tiles from the Home app before adding them back.
- **Firewall / "can't find device" during pairing.** Make sure you clicked
  **Allow** on the Windows Firewall prompt (see Installing, step 3) for
  **Private networks**. If you dismissed it or picked "Cancel", delete the
  blocked "Node.js"/"MatterHelm" entries under Windows Security → Firewall
  → Allow an app. If paired, toggle **Enable bridge** off and on; if unpaired,
  relaunch MatterHelm and choose **Pair with Google Home…** to restart it.
- **Pairing reaches "Connecting…" and then times out.** Discovery can still
  work even when the phone-to-PC unicast PASE handshake is blocked. A common
  cause is the phone being on the router's 2.4 GHz band while the PC is on
  5 GHz and band/client isolation prevents traffic between them. Put the
  phone on the same 5 GHz SSID as the PC (or disable that isolation), then
  try again.
- **The Home app says only "Can't connect" or times out before connecting.**
  The modern Home app can show this generic failure when the active VID/PID
  has no exact Matter integration in the Google Home Developer Console; it
  does not always show the older "Not a Matter-certified device" wording.
  Compare the VID/PID displayed in MatterHelm's ready-to-scan pairing window
  with the integration, including every hex digit, and make sure the phone's
  Google account belongs to that project. After any Console change, **reboot
  the Nest hub** so it discards its cached integration configuration, then
  pair again.
- **Pairing fails with "Not a Matter-certified device" as a hard error**
  (not just a warning screen you can continue past). This is the older form
  of the Developer Console identity failure described immediately above:
  the active VID/PID registration either wasn't completed, doesn't match
  exactly, or the phone's Google account isn't a member of that project.
  Correct it, then reboot the Nest hub before trying again.
<a id="ipv6-and-neighbor-discovery"></a>
- **Pairing times out immediately, or matter.js errors mention IPv6.** IPv6
  is a hard Matter requirement across the local path, not merely a PC
  checkbox. Re-enable it on the interface MatterHelm uses (adapter Properties
  → check "Internet Protocol Version 6 (TCP/IPv6)"), confirm `ipconfig` shows
  a link-local IPv6 address, and ensure the router/VLAN/Wi-Fi path between the
  phone or Nest hub and the PC passes IPv6 Neighbor Discovery (ND). Broken or
  isolated IPv6 commonly appears in Google Home as only a generic pairing
  timeout even when IPv4 works.
- **Multiple network adapters** (Wi-Fi + Ethernet, a VPN, Hyper-V/VMware
  virtual adapters). The bridge may be advertising itself on the wrong one.
  In Settings → Advanced → **mDNS network interface**, choose the real LAN
  adapter by its name and IPv4 address; saving restarts the bridge
  automatically. Use **Auto (recommended)** to return to automatic selection.
  If the current choice says **(not detected)**, the adapter was unplugged or
  renamed — select Auto or another detected adapter.
- **The tray icon is red.** Either the sidecar is crash-looping (two or more
  restarts without successfully reconnecting), or the bridge is unpaired but
  its Matter/mDNS advertisement cannot be seen. Check the app log (Settings →
  Advanced → Config folder → `logs\`) for the specific cause, and verify the
  selected network interface, IPv6, and the firewall rules above.
- **Enable bridge is checked but the icon stays gray.** The local IPC listener
  could not bind, commonly because another program already uses the configured
  port. The bridge stays disabled; check the app log, then choose a free IPC
  port in Settings → General and save.
- **A command fires twice.** MatterHelm does not run an action from its own
  automatic reset. For a normal retained custom switch, however, both user
  transitions intentionally fire once, so a routine that sends On and then
  Off runs it twice. Change the routine to send one transition, or edit the
  custom command and enable **Reset the switch after it runs (momentary
  button)**, then send On only. If one transition still produces two actions,
  check Google Home for duplicate routines and compare the two action IDs in
  MatterHelm's log; distinct IDs mean the controller sent two commands.
- **Kodi ignores or reverses Play/Pause.** Bring Kodi to the foreground. Kodi
  does not publish a Windows media session, so MatterHelm can deliver the
  focused appcommand but cannot verify playback afterward. In this path Google
  receives OK and the overlay shows success because delivery succeeded, even
  if playback did not change. The app log calls it `unverifiable`. Kodi obeys
  Play absolutely but treats Pause as a toggle; an identical dedicated verb is
  suppressed only when repeated to the same process within two seconds. A
  later Pause can resume playback, so avoid routines that send repeated Pause
  or both retained edges when Kodi is the target.
- **A key sequence logs `SendInput ... injected 0/N events (Win32 error 5)`.**
  If the target app is running elevated, Windows elevation/UIPI restrictions
  are the likely cause. `SendInput` returning zero and `GetLastError` reporting
  error 5 do not prove UIPI specifically, but the actionable remedy is to run
  the target app non-elevated (for example, clear **Run this program as an
  administrator** for Philips Hue Sync); do not elevate MatterHelm as a
  workaround. MatterHelm logs the exact key sequence, injected/expected event
  counts, and Win32 error to help diagnose the failure.
- **Need a clean slate.** Use [Factory reset](#factory-reset--re-pairing).

## Privacy notes

- Apart from its optional release check to GitHub, MatterHelm makes **no
  network calls beyond your own LAN** (Matter/mDNS to your hub) and never
  talks to any MatterHelm-operated server — there isn't one. Google's own
  Home/Assistant services are, of course, involved the same way they are for
  any Google Home device; that's between you and Google, not this app.
- The **diagnostics export** (Settings → Advanced → Export diagnostics…)
  never uploads anything — it just saves a zip to a location you choose.
  It intentionally excludes anything that could identify your PC (no
  username, no machine name, no file paths besides what you've typed into
  your own config, e.g. a custom command's launch path) and scrubs any
  commissioning codes that might otherwise have been logged. Your live
  session's pairing token is never written to disk or logged in the first
  place, so it can't leak into a bundle.
- `config.json` is included in full in a diagnostics export. It also contains
  internal preferences that are not shown as editable Settings rows, such as
  `onboardingShown` and `updateCheckEnabled`; it does **not** contain Matter
  fabric credentials, which live separately under the Matter storage folder.
