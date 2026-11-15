# ADR-015: Keep the Aggregator bridge for single-pairing control

- **Status**: accepted
- **Date**: 2026-11-15
- **Story**: S13-12

## Context

The maintainer asked whether MatterHelm could use a cleaner device model while
still pairing once for every control sequence and command. Today that one
pairing yields an Aggregator containing a Speaker and one On/Off Plug-in Unit
per command. Google Home exposes the bridge itself as an inert **Control
Bridge** and exposes its bridged devices as the usable tiles.

## Options considered

1. **Aggregator with bridged nodes.** This is the only multi-function topology
   in [Google's documented supported-device
   model](https://developers.home.google.com/matter/supported-devices): devices
   behind a Matter bridge appear as normal devices, while the bridge is an
   inert Control Bridge. Bridged endpoints obtain their names from Bridged
   Device Basic Information `NodeLabel`, and dynamic addition and removal are
   intended bridge operations.
2. **A composed node with endpoints directly under the root.** This is closer
   to the Matter model for native functions because a Bridged Node represents
   a device on a foreign network. However, same-type sibling endpoints require
   fixed Descriptor `TagList` values, while controllers including Google do
   not present `TagList` or `FixedLabel` as user-facing names. In practice,
   tiles can arrive with generic names, as reported by
   [Matterbridge issue #185](https://github.com/Luligu/matterbridge-hass/issues/185).
   Adding endpoints after commissioning may require re-pairing, as documented
   in Home Assistant Matter Hub's [standalone-device
   limits](https://riddix.github.io/home-assistant-matter-hub/getting-started/standalone-devices#notes-and-limits).
   Changing an installed bridge to this graph would make controllers treat the
   endpoints as new devices, losing room and routine assignments.
3. **One endpoint with Mode Select or derived modes.** Google Home does not
   expose Mode Select, Scenes, or Actions for app or voice control in its
   [supported-clusters catalog](https://developers.home.google.com/matter/clusters),
   and [Home Assistant Matter Hub issue
   #356](https://github.com/RiDDiX/home-assistant-matter-hub/issues/356) records
   the resulting Google naming/control gap. Generic Switch is only a routine
   starter, and video-player clusters appear only in the Home APIs catalog.
4. **One Matter node per function.** This multiplies pairings and therefore
   defeats the goal. It also multiplies the controller reconnect risk recorded
   by the S0-3 spike.

## Decision

Keep the Aggregator and bridged-node topology. One pairing continues to expose
the Speaker and every enabled command as separate controllable devices.

Enforce single-purpose bridged endpoints as an architecture invariant: every
bridged endpoint is exactly Bridged Node plus either Speaker or On/Off Plug-in
Unit. A test must assert this composition. Endpoint numbers remain stable
across restarts and when a command is removed and later re-added.

Increment the root Basic Information `ConfigurationVersion` whenever the
published endpoint composition changes. This is mandatory since
BasicInformation revision 6; it may prompt Google to rescan the device graph,
but that controller behavior is not yet proven.

## Consequences

- Users retain one pairing, individually named command tiles, and the existing
  rooms and routines through this upgrade. Google will continue to show the
  inert Control Bridge alongside those devices.
- The single-purpose rule is deliberately stricter than matter.js permits. A
  late-October 2026 Google fresh-commissioning stall correlates with bridged
  endpoints that carry several device types in [Matterbridge issue
  #680](https://github.com/Luligu/matterbridge/issues/680) and [Home Assistant
  Matter Hub issue
  #549](https://github.com/RiDDiX/home-assistant-matter-hub/issues/549).
  The cause is unconfirmed, while plug and bulb bridges were unaffected.
- Stable endpoint-number allocation becomes persisted compatibility state, not
  an implementation detail. Removing a command reserves its number for that
  command's return.
- Dynamic add/remove remains supported without re-pairing, but controller
  discovery is asynchronous and must be verified against current Google hub
  firmware.
- Revisit this decision only if Google documents composed-device naming or a
  multi-command cluster suitable for app and voice control.
