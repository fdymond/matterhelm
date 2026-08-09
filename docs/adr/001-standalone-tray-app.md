# ADR-001: Standalone tray application, independent of VoiceRemote

- **Status**: accepted
- **Date**: 2026-07-26
- **Story**: pre-Sprint-0 (owner direction)

## Context

The original blueprint made this project a *sidecar* of VoiceRemote: the Matter
bridge forwarded Google Home actions over IPC into VoiceRemote's
`CommandRouter`, and VoiceRemote supplied the tray UI, overlay, config, and
process supervision. The owner has directed that the Google Home / Matter
integration be **its own app, fully independent of the VoiceRemote repo and
code**, with its own tray application and its own command/transcribed-input
overlay pop-ups.

## Decision

This repo ships a complete, self-sufficient product with two processes:

1. **`app/` — C# .NET 8 WinForms tray application** (the product): supervises
   the bridge sidecar, executes all actions itself (SMTC media sessions, media
   keys, CoreAudio volume/mute, display/power), owns config
   (`%APPDATA%\HtpcMatterBridge\config.json`, now `%APPDATA%\MatterHelm`
   since the S7-2 rename), the pairing-QR UX, and a
   click-through **overlay HUD** that flashes each incoming Google Home command
   and the action taken (same UX pattern as VoiceRemote's transcript pop-ups,
   implemented fresh in this repo).
2. **`bridge/` — Node 22 + matter.js sidecar** (unchanged role): the Matter
   Aggregator with Speaker + momentary-switch endpoints, talking to the tray
   app over the localhost WS protocol (protocol unchanged — the supervisor is
   now this repo's own app instead of VoiceRemote).

No references to VoiceRemote code, config, or processes. Patterns may mirror
VoiceRemote where they are good patterns (flash HUD, tray lifecycle, rolling
log), but every line is written and owned here. The two apps run side-by-side
on the same HTPC without knowledge of each other: VoiceRemote = voice input,
this app = Google Home input; both dispatch to the same OS media facilities.

## Consequences

- **Easier**: independent release cadence; no cross-repo coordination; each app
  testable and shippable alone; a VoiceRemote regression can never break Google
  Home control (and vice versa).
- **Harder**: duplicated (fresh) implementations of media/volume execution,
  tray plumbing, and overlay HUD (~2–3 days of extra C# work in Sprint 2);
  users running both apps maintain two configs.
- **Impossible now**: Google Home actions routing through VoiceRemote's phrase
  map / Kodi-aware smart routing. Kodi support here, if wanted, becomes its own
  backlog item.
- BLUEPRINT.md §2 rewritten; Sprint 2 of the plan and backlog re-scoped from
  "VoiceRemote integration" to "tray app + executor + overlay" in this repo.
