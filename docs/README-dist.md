# MatterHelm — quick start

MatterHelm turns a Windows PC into a locally paired Google Home Matter bridge.
It runs as a tray app and needs no separate Node.js or .NET installation.

## Prerequisites

- Windows 10 version 1809 (build 17763) or later, x64.
- A Google/Nest Matter hub on the same LAN; a phone alone is not enough.
- IPv6 enabled on the selected LAN adapter and across the local path.
- Google Home app access to the home and a free Google Home Developer Console
  Matter integration whose VID/PID matches MatterHelm's defaults
  (`0xFFF1`/`0x8000`).

## Package contents

The portable zip contains the self-contained published tray-app files,
`MatterHelm.exe`, and either a bundled sidecar executable or the supported
fallback sidecar bundle/runtime under `sidecar\`. It also includes `LICENSE`,
`NOTICE`, and this `README-dist.md`. Keep the extracted files together.

## First run and pairing

1. Extract the complete zip to a writable folder and run `MatterHelm.exe`.
2. Right-click the helm tray icon and choose **Pair with Google Home…**. On an
   unpaired install this is the bridge-start action: it enables and persists
   the bridge and opens the pairing window. **Enable bridge** is hidden until
   commissioning succeeds.
3. Allow the Windows Firewall prompt for **Private** networks.
4. Wait for the tray to turn blue and for the QR/manual code to appear, then in
   Google Home choose **+ Add → Matter-enabled device** and scan or enter it.
5. Complete room/name setup. The tray turns green when commissioned and
   connected.

Tray states: **gray** = bridge disabled; **amber** = starting or awaiting
lifecycle status; **blue** = healthy and awaiting pairing; **green** = paired
and connected; **red** = crash/restart loop or commissionable advertisement
missing.

Play/Pause is retained: On requests Play and Off requests Pause in the focused
program first. Windows media-session checks/fallback are ownership-aware, so a
different app's session cannot suppress or prove the command. Sessionless
players are delivered to but explicitly unverifiable. Next/Previous
retain state and fire on either user transition.
Power is reversible for displays-off, pause-plus-displays-off, and screensaver
modes; sleep fires once on Off and promptly returns the tile to On. Custom
commands are retained/both-edge by default and can opt into **Reset the switch
after it runs** in Settings → Custom devices.

For setup, upgrade notes, display behavior, factory reset, and troubleshooting,
see the full user guide in the repository or release page:
<https://github.com/fdymond/matterhelm/blob/main/docs/user-guide.md>.
