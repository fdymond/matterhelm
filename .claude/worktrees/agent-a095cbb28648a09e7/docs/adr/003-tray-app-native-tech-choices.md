# ADR-003: Tray-app native technique choices (WS server, CoreAudio, input, overlay, QR, publish)

- **Status**: accepted
- **Date**: 2026-07-26
- **Story**: pre-Sprint-2 (integrator feasibility research, 2026-07-26)

## Context

BLUEPRINT §2.4 names components but not the concrete Windows techniques.
Research (MS Learn + primary issue trackers; sources in the research report)
settled each open question; fixing them now prevents Sprint-2 agents from
re-deciding or picking footgun variants.

## Decision

1. **IPC WebSocket server**: `HttpListener` + `AcceptWebSocketAsync` with the
   prefix `http://localhost:{port}/` — the `localhost` string prefix is exempt
   from http.sys URL-ACL reservations, so no admin rights. Never use the
   `127.0.0.1` literal in the prefix (that namespace needs an ACL). Loopback
   binding + token hello preserved. Fallback (only if HttpListener misbehaves):
   raw `TcpListener` + RFC6455 handshake + `WebSocket.CreateFromStream`.
2. **Volume/mute**: pure COM interop (`IMMDeviceEnumerator`,
   `IAudioEndpointVolume`, `IAudioEndpointVolumeCallback`) — no NAudio.
   Mandatory care: keep a strong ref to the registered callback (GC),
   `UnregisterControlChangeNotify` on dispose, marshal `OnNotify` to the UI
   thread, and register `IMMNotificationClient` to re-acquire the endpoint on
   default-device change.
3. **Media transport**: `SendInput` with `VK_MEDIA_*` is the primary and only
   mechanism (system-wide, same path as hardware keys). Do NOT use
   SMTC/`GlobalSystemMediaTransportControlsSessionManager` — wrong tool
   (session-scoped, misses non-SMTC apps) and would force a versioned
   `net8.0-windows10.x` TFM. Keep the plain `net8.0-windows` TFM.
4. **Display/power**: `SC_MONITORPOWER 2` to a dedicated message-only window
   (not `HWND_BROADCAST`) to blank; wake via `SendInput` relative mouse nudge
   (`SC_MONITORPOWER -1` is unreliable since Win8). Sleep via WinForms
   `Application.SetSuspendState` (handles the shutdown-privilege enable).
5. **Overlay HUD**: `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE |
   WS_EX_TOOLWINDOW` via `CreateParams`, `ShowWithoutActivation => true`,
   `WM_MOUSEACTIVATE => MA_NOACTIVATE`. Per-pixel alpha via
   `UpdateLayeredWindow` (premultiplied 32bpp DIB) for fades; never mix with
   `Form.Opacity`/`SetLayeredWindowAttributes` on the same HWND.
6. **QR rendering**: accept **QRCoder** (MIT, active) as the single justified
   NuGet exception, using the `PngByteQRCode` renderer (no System.Drawing
   coupling) into a `PictureBox`. A from-scratch ISO 18004 encoder is not a
   sane use of a story.
7. **Publish** (S3-1): CLI `dotnet publish -c Release -r win-x64
   --self-contained true -p:PublishSingleFile=true
   -p:IncludeNativeLibrariesForSelfExtract=true`; never `PublishTrimmed`
   (WinForms COM). Expect 60–100 MB; first-run temp extraction is normal.

## Consequences

- **Easier**: Sprint-2 stories start from settled techniques with the known
  pitfalls listed as review checkpoints; zero NuGets except QRCoder.
- **Harder**: hand-rolled CoreAudio interop is the hairiest piece (~4 COM
  interfaces) — S2-2 review must check ref-counting and thread marshalling.
- **Rollback**: each choice has a named fallback (TcpListener WS, NAudio
  CoreAudioApi subset) that swaps behind the same component interface without
  touching the protocol or UI.
