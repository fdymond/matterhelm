# ADR-011: Blue "awaiting pairing" tray state (deviation from BLUEPRINT amber)

- **Status**: accepted
- **Date**: 2026-08-26
- **Story**: S10-10c (owner-directed)

## Context

The BLUEPRINT's tray-icon state table colors the enabled-but-uncommissioned
bridge amber, folding it into the generic "starting / not ready" family. The
2026-08-25/26 pairing outage showed why that folding hurts: a bridge that is
healthy and advertising but simply *not yet paired* is indistinguishable at a
glance from one that is still booting or unhealthy, and the owner spent hours
unsure whether the bridge side was even alive.

## Decision

Introduce a distinct `AwaitingPairing` tray state, rendered BLUE with tooltip
"running, not paired yet", selected when the sidecar is authenticated and
`matterStatus` reports `commissioned=false`. Amber remains the pre-hello
startup state; the commissioned/connected, disabled, and error states are
unchanged. Requested explicitly by the owner ("if the matter bridge is not
paired it should show in a blue colour … to indicate it is enabled and running
but not yet paired").

## Consequences

- BLUEPRINT §2.4 and the user-guide now incorporate this accepted state.
- The blue state derives from the additive `matterStatus` frame (protocol v3),
  Protocol v2 shipped in 0.1.0 and v3 shipped in 0.4.2. A stale v2 sidecar
  would never leave amber and is rejected by the current exact-v5 parser.
- Tests: state derivation pinned in BridgeHostTests.StateDerivation;
  tray/tooltip behavior in TrayContextTests.
