---
name: matterjs-upgrade
description: Guides a matter.js (@matter/*) version upgrade in the bridge, including the empirical ADR-010 Windows mDNS re-verification and Matter behavior checks.
when_to_use: Use whenever changing any @matter/* dependency version.
---

# Upgrade matter.js

Keep the matter.js change isolated from Node, TypeScript, Vitest, and unrelated dependency
upgrades so failures and compatibility changes have one cause.

## Prepare and inspect

1. Read the target matter.js release notes and migration guidance for breaking changes.
2. Record the old and new exact `@matter/*` versions and keep the lockfile consistent.
3. Run tests with endpoint validation strict; do not weaken validation to accept new behavior.

## Device and persistence checks

- Verify exact-value requirements for every advertised device type, including Speaker
  LevelControl limits and other fixed cluster attributes.
- Exercise dynamic endpoint add/remove, including `parts.delete`, `parts.clear`, and safe
  endpoint-number reuse after removal and restart.
- Start with existing commissioned storage and prove that fabric, endpoint identity, and
  controllability survive the upgrade and another restart.

## ADR-010 Windows mDNS matrix

Compare an unpatched build with the ADR-010 shim; do not evaluate only the patched path.
For each build, test both auto-selected and explicitly pinned NICs, and cover IPv4 and IPv6.

- Capture an inbound `_matterc` query and the corresponding outbound response latency.
- Sustain queries beyond the post-start announcement burst so announcements cannot masquerade
  as query handling.
- Confirm numeric IPv6 scope IDs resolve to interface records and both response families select
  the intended interface.
- Keep the workaround unless the unpatched build answers inbound queries correctly across the
  matrix; startup announcements alone are insufficient evidence.

## Hardware and record

With a human, commission through real Google Home, execute controls, restart the app and
sidecar, verify control again, then factory reset and commission afresh. Hardware steps remain
unverified until the human performs them.

Record versions, NIC modes, address families, packet evidence, response latency, persistence,
Google Home, restart, and factory-reset results in ADR-010. State which wrappers remain and why.
Run the `merge-gates` skill before integration.
