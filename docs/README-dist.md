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
   `MatterHelm.exe`. A tray icon appears.
2. In the tray menu, enable the bridge. The icon turns amber while starting,
   green once the sidecar link is up.
3. Choose **Pair…** in the tray menu, then in the Google Home app: add
   device → "Works with Google / Matter-enabled device" → scan the QR code
   (or type the manual code).
4. Say "Hey Google, set HTPC volume to 40 %" — the overlay HUD on the PC
   shows each incoming command.

Voice phrases and routine ideas: `docs/routines.md` in the source repository
(https://github.com/fdymond/matterhelm).

Settings (device names, custom commands, power-off behavior, overlay) live in
the tray menu's **Settings…** window. Logs and diagnostics export are under
**Diagnostics…**.
