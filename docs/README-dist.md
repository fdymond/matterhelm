# MatterHelm — quick start

MatterHelm turns this Windows PC into a locally-paired Google Home device:
"Hey Google, set HTPC volume to 40 %", Home-app taps, and routines execute
media/volume/power actions directly on the PC. Local network only — no cloud
account link, no OAuth.

## Prerequisites

- Windows 10/11 x64. No Node.js or .NET install needed — everything is in
  this folder.
- A Google **Matter hub** device on the same LAN: Nest speaker/display
  (e.g. Nest Hub), Nest Wifi Pro, or Google TV Streamer.
- A one-time, **free** [Google Home Developer Console](https://console.home.google.com/)
  project with the bridge's test VID/PID (`0xFFF1` / `0x8000`) registered as
  a Matter integration. Google refuses to commission an uncertified device
  without this — it is a five-minute, no-cost setup.

## Run

1. Put this folder anywhere (e.g. `C:\Program Files\MatterHelm`) and start
   `MatterHelm.exe`. A tray icon appears, and on a fresh install the **setup
   guide** opens with the three steps below. (Reopen it any time: tray menu →
   **Setup guide…**.)
2. **Register once with Google** — free, ~5 minutes, once per account, at
   [console.home.google.com](https://console.home.google.com/): create a
   project → Add integration → Matter → VID `0xFFF1`, PID `0x8000`. Skipping
   this is the usual cause of a hard "not certified" pairing failure.
3. In the tray menu, enable the bridge (allow the **Private networks**
   firewall prompt). The icon turns amber while starting, green once the
   sidecar link is up.
4. Choose **Pair with Google Home…** in the tray menu, then in the Home app:
   **+ Add** → **Add device** → **Matter-enabled device** → scan the QR code
   (or type the manual code). The pairing window tells you where it's up to.
5. Say "Hey Google, set HTPC volume to 40 %" — the overlay HUD on the PC
   shows each incoming command.

Voice phrases and routine ideas: `docs/routines.md` in the source repository
(https://github.com/fdymond/matterhelm).

Settings (device names, custom commands, power-off behavior, overlay) live in
the tray menu's **Settings…** window. Logs and diagnostics export are under
**Diagnostics…**.
