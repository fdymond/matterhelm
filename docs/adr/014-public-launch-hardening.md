# ADR-014: Public-launch hardening and bounded trust boundaries

- **Status**: accepted
- **Date**: 2026-09-10
- **Story**: S12-4 (public-switch code-review remediation)
- **Amends**: [ADR-004](004-settings-ui-and-custom-commands.md),
  [ADR-006](006-telemetry-and-diagnostics.md), and
  [ADR-007](007-resource-budgets-measured.md)
- **Related, unchanged**:
  [ADR-013](013-focused-first-media-and-retained-mouse-move.md)

## Context

The review before switching the repository from private to public found places
where a narrow local assumption was documented but not enforced, diagnostic
artifacts could retain more identity than intended, and bursty or stalled work
could grow queues or block shutdown. It also found release-supply-chain gaps
and large app façades that made the safety rules difficult to review. These are
unreleased hardening changes on top of v0.7.1; they do not change IPC revision
5 or the Matter endpoint model.

## Decision

1. **Enforce loopback admission.** Keep the URL-ACL-free `localhost` HTTP.sys
   prefix, but reject every non-loopback remote endpoint with HTTP 403 before
   taking the one-client slot. Log one WARN containing the address family only.
2. **Fail closed on tray frames.** The Node client uses `ws` (Node's built-in
   WebSocket cannot send policy code 1008). Its first binary, malformed-JSON,
   or schema-invalid inbound frame closes with 1008; bounded reconnect remains
   the recovery path. This amends ADR-004's IPC-client behavior.
3. **Make diagnostics private by default.** Export a structurally useful but
   sanitised config by default: redact `uniqueIdSeed`, launch arguments, and
   profile paths. Raw config needs an explicit code-level opt-in and has no UI.
   Stream every bundled log/metrics line through case-insensitive redaction of
   commissioning credentials (including text/JSON-key `discriminator` values),
   profile paths, and IPv4/IPv6 literals. Replace standalone or long QR block-
   art lines with `<qr-art>`; replace machine/user names at word boundaries,
   while full profile-path replacement still covers path segments. This amends
   ADR-006 §2.
4. **Crash recoverably.** An unhandled sidecar promise rejection logs and exits
   non-zero so the existing supervisor backoff restarts a clean process.
5. **Bound pipelines.** Serialize sidecar Speaker writes with one coalescing
   latest-state slot; cap echo expectations at 32 with a five-second TTL. The
   tray keeps one state send in flight plus one latest pending state and gives
   sends five seconds. Delay-bearing macros are single-flight per command and
   capped at eight globally. App logs retain seven days and twenty 5 MiB
   segments/day with oldest-first eviction; identical WARN/ERROR bursts write
   20 lines/minute, then produce `suppressed <N> repeats of: <message>`. Cache
   a failed segment eviction for 30 seconds without dropping writes, and flush
   a pending repeat summary at orderly exit or day rollover.
6. **Bound configuration.** Limit `config.json` to 1 MiB, custom commands to
   64, names to 64 characters, launch arguments to 2048 characters, and the
   identity seed to 128 characters. Invalid app fields degrade to validation/
   WARN errors; the sidecar independently rejects invalid shared endpoint/name/
   seed values. It never receives the config file or launch actions. Move a
   file that is oversized, invalid JSON, or not a JSON object to
   `config.json.rejected-<timestamp>`, run on in-memory defaults, and refuse
   every save with the recovery message until a later successful load. Surface
   the condition once as a startup WARN. Because that latch also blocks the
   newly minted identity seed from being persisted, users must restore or
   delete the rejected file and restart before pairing.
7. **Preserve user intent on port conflicts.** An address-in-use start becomes
   a red error state, logs the specific reason, and leaves `BridgeEnabled`
   unchanged; a competing process or Windows/RDP session must not silently
   persist a user disable.
8. **Stage factory reset.** Atomically rename live Matter storage before
   recursive deletion. Rename failure touches nothing and fails the reset;
   cleanup failure completes the reset and logs `residue left at '<path>'`.
9. **Declare and reconcile distribution contents.** Generate and ship
   `THIRD-PARTY-NOTICES.txt` for bundled npm packages (including matter.js),
   Node.js, QRCoder, the .NET and WindowsDesktop runtimes, and the Windows SDK
   projection. Generate `sidecar-layout.json` (`sea` = `bridge.exe`, `node` =
   `node.exe` + `bridge.cjs`); select from it and remove the other known layout
   on installer and portable upgrades. Pin/invoke `postject` locally; its
   failure fails packaging unless `-AllowNodeLayoutFallback` explicitly opts
   into the Node/bundle layout.
10. **Harden updater handoff.** Remove `MATTERHELM_UPDATE_TOKEN` from sidecar
    and helper environments, and keep the downloaded package protected by an
    open read handle from hashing through installer launch or archive expansion.

## Consequences

Users get safer diagnostics, truthful port-conflict/reset outcomes, predictable
storage/log growth, and layout-changing upgrades without stale sidecars.
Contributors get explicit queue/config budgets and smaller app boundaries:
`BridgeHost` now composes `BridgeLifecycleCoordinator`,
`BridgeActionDispatcher`, and `VolumeStatePublisher`; `MediaKeys` fronts the
focused routing collaborators; settings, restart, launch-environment, and
serial-queue policies live in named components. ADR-013's media and retained-
mouse decisions are unchanged.

IPC sends now return `false` unless the socket is actually OPEN, and a local
policy close preserves reconnect backoff so a peer cannot reset it by sending
one valid frame before each rejected frame.

The ADR-007 resource and latency budgets still apply, but must be re-measured
after these changes; the integrator will publish fresh counts and measurements.
Two items are deferred: `SHA256SUMS.txt` is not an independent authenticity
root, so Authenticode or a separately signed release manifest is a maintainer
decision (BACKLOG Proposed: SF-1); IPC frame-buffer pooling waits for a
benchmark that proves it worth the complexity (BACKLOG Proposed: M-8).
