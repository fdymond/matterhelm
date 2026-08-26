# ADR-011: Blue "awaiting pairing" tray state (deviation from BLUEPRINT amber)

Date: 2026-08-26 · Status: accepted · Owner-directed (S10-10c)

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

- BLUEPRINT §tray-state table is superseded on this one row by this ADR; the
  user-guide icon legend documents blue (S10-13 docs task).
- The blue state derives from the additive `matterStatus` frame (protocol v3),
  so a v2 sidecar (never shipped) would simply never leave amber — acceptable.
- Tests: state derivation pinned in BridgeHostTests.StateDerivation;
  tray/tooltip behavior in TrayContextTests.
