# ADR-003: Tray-app native technique choices (WS server, CoreAudio, input, overlay, QR, publish)

- **Status**: accepted; media-transport choice superseded by
  [ADR-013](013-focused-first-media-and-retained-mouse-move.md)
- **Date**: 2026-07-26
- **Amended**: 2026-08-30 by S10-31 and
  [ADR-013](013-focused-first-media-and-retained-mouse-move.md)/S11-3
- **Story**: pre-Sprint-2 (integrator feasibility research, 2026-07-26); S10-31; S11-2; S11-3

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
> **Superseded media design:** item 3 is retained as history. The current
> same-app fallback rules and four-second deadline are defined by
> [ADR-013](013-focused-first-media-and-retained-mouse-move.md).

3. **Media transport**: `SendInput` with `VK_MEDIA_*` remains the system-wide
   mechanism for next, previous, and stop. Play, Pause, and Play/Pause first
   send bounded `WM_APPCOMMAND` to the captured foreground HWND. The tray
   resolves that window's PID/executable/AUMID and compares it with every
   current session's `SourceAppUserModelId`; only a same-app session may
   short-circuit or verify focused delivery. Integrator measurements against Spotify and
   YouTube found
   `APPCOMMAND_MEDIA_PLAY` produced Paused→Playing, Playing→Paused,
   Paused→Playing when delivered exactly like the app: it toggles despite its
   dedicated name. Repeated SMTC calls remained Playing→Playing and
   Paused→Paused, confirming absolute behavior. Therefore every session
   fallback uses `TryPlayAsync`/`TryPauseAsync`, including a Play/Pause desired
   state computed before focused delivery; a fallback toggle is forbidden.
   A different-app session is ignored after successful focused delivery, but
   becomes the intended target if focused delivery itself fails. The captured
   session identity is pinned across action and verification, and the whole route
   has one three-second cancellation deadline. Sessionless focused delivery is
   acknowledged as unverifiable, not playback-verified. The WinRT projection is compiled by the current versioned
   `net10.0-windows10.0.17763.0` TFM.
4. **Display/power**: DDC/CI VCP `0xD6` is primary for each accepting physical
   display. Only when no display accepts DDC/CI, use `SC_MONITORPOWER 2` to a
   dedicated message-only window (not `HWND_BROADCAST`) to blank globally;
   wake via `SendInput` relative mouse nudge (`SC_MONITORPOWER -1` is
   unreliable since Win8). Mixed setups leave unsupported panels on. Sleep
   uses WinForms `Application.SetSuspendState`.
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
- **Focused-player limitation (superseded by
  [ADR-013](013-focused-first-media-and-retained-mouse-move.md))**: non-SMTC apps such as Kodi receive the
  appcommand but cannot be verified. Kodi's measured Pause behavior is a
  toggle; immediate identical dedicated verbs to the same process are
  suppressed for two seconds, but later repeats can still invert playback.
  Logs and UI documentation distinguish delivery from verified state. A
  wedged session cannot exceed the route's three-second deadline.
- **Rollback**: each choice has a named fallback (TcpListener WS, NAudio
  CoreAudioApi subset) that swaps behind the same component interface without
  touching the protocol or UI.
