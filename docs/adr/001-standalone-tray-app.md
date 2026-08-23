# ADR-001: Standalone tray application

- **Status**: accepted
- **Date**: 2026-07-26
- **Story**: pre-Sprint-0 (owner direction)

## Context

The original blueprint made this project a *sidecar* of a separate,
pre-existing (private) voice-control application on the same machine: the
Matter bridge forwarded Google Home actions over IPC into that app's command
router, and that app supplied the tray UI, overlay, config, and process
supervision. The owner directed that the Google Home / Matter integration be
**its own app, fully independent of any other repository or code**, with its
own tray application and its own command overlay pop-ups.

## Decision

This repo ships a complete, self-sufficient product with two processes:

1. **`app/` — C# .NET WinForms tray application** (the product): supervises
   the bridge sidecar, executes all actions itself (media keys, CoreAudio
   volume/mute, display/power), owns config
   (`%APPDATA%\HtpcMatterBridge\config.json`, now `%APPDATA%\MatterHelm`
   since the S7-2 rename), the pairing-QR UX, and a click-through **overlay
   HUD** that flashes each incoming Google Home command and the action taken.
2. **`bridge/` — Node 22 + matter.js sidecar** (unchanged role): the Matter
   Aggregator with Speaker + momentary-switch endpoints, talking to the tray
   app over the localhost WS protocol (protocol unchanged — the supervisor is
   now this repo's own app).

No references to any external app's code, config, or processes — every line
is written and owned here. Other automation tools may run side-by-side on the
same HTPC without knowledge of this app; all of them ultimately dispatch to
the same OS media facilities.

## Consequences

- **Easier**: independent release cadence; no cross-repo coordination; the
  app is testable and shippable alone; nothing outside this repo can break
  Google Home control (and vice versa).
- **Harder**: fresh implementations of media/volume execution, tray plumbing,
  and overlay HUD (~2–3 days of extra C# work in Sprint 2) rather than
  reusing an existing app's.
- **Impossible now**: routing Google Home actions through any external app's
  phrase map or player-aware smart routing. Kodi support here, if wanted,
  becomes its own backlog item.
- BLUEPRINT.md §2 rewritten; Sprint 2 of the plan and backlog re-scoped from
  "external-app integration" to "tray app + executor + overlay" in this repo.
