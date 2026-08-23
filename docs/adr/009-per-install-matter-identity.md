# ADR-009: per-install Matter identity, configurable VID/PID

- **Status**: accepted
- **Date**: 2026-08-23
- **Story**: S10-4 (owner-directed, from the S10 portability review)

## Context

A pre-launch review asked whether an install works on someone else's Google
Home account, and whether this machine's config is bound to the owner's
setup. Most of the answer was clean — pairing codes are minted per install
at first boot (verified: two fresh sidecars produced different QR/manual
codes), fabric credentials live only in `%APPDATA%`, the shipped artifact
carries no state, and config holds preferences only.

One defect surfaced. `bridge.ts` derived every endpoint's `serialNumber` /
`uniqueId` from a compiled-in constant seed (`"htpc-matter-bridge"`) that no
caller ever overrode, so **every MatterHelm install on earth derived
byte-identical identities**:

```
speaker  serial=htpc-speaker-bc87a72c  unique=e083bb5c2384b1a2   (on every install)
```

Across separate homes that is invisible — fabrics never meet. Within one
home it is a spec violation: Matter requires `BridgedDeviceBasicInformation.
UniqueID` to identify a bridged device, and two MatterHelm PCs would present
the same ids under the same VID/PID, leaving Google Home no way to tell them
apart.

Separately, ADR-002 claimed the test VID/PID were "both configurable in
bridge config". `BridgeOptions` accepted them, but nothing exposed them —
they were effectively fixed at `0xFFF1`/`0x8000`.

## Decision

**1. The identity seed becomes per-install, resolved once, migration-safe.**
`MatterIdentity.Resolve` decides on the first run that has no seed persisted:

| Situation | Seed |
|---|---|
| Config already has a seed | that seed (an earlier run's or the user's decision) |
| No seed, **Matter storage exists** | `LegacySeed` (`"htpc-matter-bridge"`) |
| No seed, no storage (fresh install) | a freshly minted random seed |

The middle row is the whole point: changing the seed of an already-paired
install re-identifies every endpoint, which Google reads as different
devices — silently unpairing the user. Existing installs therefore keep the
identity Google already has, and only genuinely fresh installs get a unique
one. Either way the answer is persisted to `config.json`, so it is decided
once and never re-inferred.

**2. VID/PID become real config**, defaulting to the ADR-002 test pair, with
`HTPC_BRIDGE_VENDOR_ID` / `HTPC_BRIDGE_PRODUCT_ID` env vars and Settings →
Advanced rows. Both accept hex (`0x8003`) or decimal, because the Google
Home Developer Console shows hex and JSON has no hex literal. Values outside
the sanctioned test ranges are *allowed* (someone may own an allocated VID)
but are not the default.

**3. The env contract grows three variables** (BLUEPRINT §2.3). All three are
optional: unset means "the bridge's own default", so a standalone sidecar run
behaves exactly as before.

## Consequences

- **Easier**: two PCs in one home can each pair — distinct seeds give
  distinct endpoint identities (verified: legacy + two minted seeds produce
  0 collisions across 15 endpoint ids), and a distinct PID from the test
  range separates them further. Anyone registering their own Developer
  Console project can point the app at their own VID/PID without a rebuild.
- **Harder**: three more env vars and a config field to keep in lockstep
  across the two processes; identity is now data rather than a constant, so
  a corrupted/hand-edited seed is a way to unpair yourself. The Settings
  rows are flagged as bridge-restart and warn that changing them re-pairs.
- **Unchanged**: existing installs (this machine included) keep their
  pairing — verified live: after upgrading the paired HTPC install, the log
  read *"existing fabric found — pinned to the legacy shared seed (pairing
  preserved)"* and the hub re-subscribed to the same fabric.
- **Rollback**: delete `uniqueIdSeed` from `config.json` on an install whose
  fabric still exists and the legacy constant is re-pinned on next start;
  reverting the code entirely restores the old constant-seed behavior with
  no storage migration.
- **Amends ADR-002**, which claimed a VID/PID knob that did not exist —
  it exists now.
