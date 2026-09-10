# ADR-006: Development telemetry, diagnostics, and the reconnect-latency verdict

- **Status**: accepted; diagnostics privacy amended by
  [ADR-014](014-public-launch-hardening.md)
- **Date**: 2026-07-29
- **Story**: S5-0 (maintainer-directed; two cited research reports, 2026-07-29)

## Context

The maintainer wants full development telemetry/debugging/logging, analytics,
coverage and validation. Research (Matter/matter.js + .NET 10 diagnostics,
primary sources) fixed the design. A separate finding re-scopes the
"flaky/slow hub" complaint (§4).

## Decision

### 1. Bridge diagnostics (S5-1)

- Bump `@matter/main` 0.17.6 → **0.17.7** (routine patch pin; subscription
  expiry fix, per-node `sessions.intervals`, diagnostics fixes; no ADR-level
  deviation).
- matter.js logs flow through **`Logger.destinations`** into pino as
  structured events (no more raw console text), with
  **`Logger.facilityLevels`** exposed via `HTPC_BRIDGE_MATTER_LOG_LEVEL`
  (global) and `HTPC_BRIDGE_MATTER_LOG_FACILITIES` (JSON map) so mDNS/session/
  interaction facilities can be raised to debug selectively.
- **Session observability**: session/subscription lifecycle (established,
  closed, subscription created/expired) logged as pino events with
  timestamps — the instrument for the hub re-association stalls.
- **Per-action timing**: cluster write → WS send elapsed (µs) logged with the
  action frame's existing `id`. The action `id` (uuid, echoed in acks) IS the
  cross-process correlation id — **no protocol change**; W3C traceparent is
  deliberately skipped (single loopback hop).
- Sidecar log level continues to arrive via env at spawn; runtime level
  switching would need a protocol addition and is deferred (a settings change
  + bridge restart applies it).

### 2. App diagnostics (S5-2)

- **Timing**: frame received → executed → acked via
  `Stopwatch.GetTimestamp/GetElapsedTime`, logged per action id.
- **Metrics**: in-box `System.Diagnostics.Metrics` `Meter`
  (`HtpcMatterBridge`, now `MatterHelm` since the S7-2 rename): counters
  (actions ok/failed, acks, supervisor
  restarts, IPC connects/disconnects, state frames published/suppressed) +
  latency histogram. Consumed in-process by a `MeterListener` writing a
  JSON-lines metrics file next to the logs, and externally via
  `dotnet-counters` for live inspection. **No OpenTelemetry SDK** (local-only
  capture; OTel exporters are precisely the upload path we exclude).
- **Log levels**: the static `Log` gains `Debug` + a configurable minimum
  level (`appLogLevel` in config + Settings row, applies live). Full
  Microsoft.Extensions.Logging migration is deliberately deferred (backlog)
  — category filtering isn't yet worth churning ~100 call sites; revisit if
  categories are needed.
- **Diagnostics bundle**: Settings → Advanced → **Export diagnostics** zips
  app+sidecar logs, metrics files, an environment manifest (OS, .NET runtime,
  app version, sidecar version when available, locale, and a UTC generation
  timestamp; no username-bearing paths), and the
  config. Privacy: the IPC token is runtime-only and must be unreachable from
  the bundle path — enforced by a test that scans a produced bundle for the
  live session token. Everything stays local; nothing uploads.

> **Public-launch amendment:** ADR-014 makes the config sanitised by default
> (identity seed, launch arguments, and profile paths redacted), with raw config
> available only through an explicit code-level opt-in and no UI. Every bundled
> log/metrics line now also redacts commissioning credentials, machine/user
> names, profile paths, and IP literals case-insensitively.

### 3. Coverage & validation (S5-3)

- C#: `coverlet.collector` + `dotnet test --collect:"XPlat Code Coverage"`
  (VSTest mode — **MTP coverage is xunit-v3-only**, and xunit v3 migration
  stays a separate future ADR). ReportGenerator publishes a markdown summary
  to the Actions step summary; thresholds enforced.
- Bridge: vitest's v8 provider (already AST-accurate) publishes the same way.

### 4. Reconnect-latency verdict (risk register)

Cross-vendor evidence (matter.js controller ≈2 min, Apple ≈10–15 min, Nest
Hub 2 >60 min/never in our spike): the post-restart re-association stall is
**Google controller policy**, not bridge-tunable. matter.js server shutdown is
protocol-silent by design (no peer goodbye exists in the spec), so graceful
stops don't help either. Bridge-side we keep: stable identity across restarts
(spike-proven), periodic operational mDNS re-announcement, and 0.17.7's
observability to capture any future stall. **No further sprint time on
bridge-side subscription tuning for this.** Practical mitigation: hub
power-cycle / Home-app toggle.

### 5. Spikes & watch items

- **Spike (Windows)**: verify which IPv6 address matter.js advertises in AAAA
  records — RFC 8981 privacy addresses now rotate ~2-daily on Windows; if the
  temporary address is picked, operational addresses go stale mid-session
  (disable privacy extensions on the bridge NIC if confirmed).
- **Watch**: matter.js Matter-1.6 `DeviceLoadStatus` + IM counters (wire into
  diagnostics once in a stable release); Google adopting HA-style rejection
  of test-certificate devices at (re)commissioning time (would affect future
  re-pairing, not the existing fabric).

## Consequences

- **Easier**: latency/staleness becomes measurable end-to-end by action id;
  hub stalls produce session-event evidence; zero new runtime dependencies
  (coverlet/ReportGenerator are test/CI-only).
- **Harder**: two log surfaces (app file + metrics file) to rotate/prune; the
  diagnostics bundle adds a privacy-sensitive code path (S5-R reviews it).
- **Rollback**: instrumentation is additive and behind log levels; the meter
  and listener can be deleted without touching behavior.
